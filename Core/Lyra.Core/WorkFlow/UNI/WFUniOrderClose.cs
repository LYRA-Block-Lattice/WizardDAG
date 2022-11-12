﻿using Lyra.Core.API;
using Lyra.Core.Blocks;
using Lyra.Core.Decentralize;
using Lyra.Data.API;
using Lyra.Data.API.WorkFlow;
using Lyra.Data.API.WorkFlow.UniMarket;
using Lyra.Data.Blocks;
using Lyra.Data.Crypto;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lyra.Core.WorkFlow.Uni
{
    [LyraWorkFlow]//v
    public class WFUniOrderClose : WorkFlowBase
    {
        public override WorkFlowDescription GetDescription()
        {
            return new WorkFlowDescription
            {
                Action = BrokerActions.BRK_UNI_ORDCLOSE,
                RecvVia = BrokerRecvType.DaoRecv,
                Steps = new [] { SealOrderAsync, SendCollateralToSellerAsync}
            };
        }

        public override async Task<APIResultCodes> PreSendAuthAsync(DagSystem sys, SendTransferBlock send, TransactionBlock last)
        {
            if (send.Tags.Count != 3 ||
                !send.Tags.ContainsKey("daoid") ||                                            
                !send.Tags.ContainsKey("orderid") ||
                string.IsNullOrWhiteSpace(send.Tags["orderid"])
                )
                return APIResultCodes.InvalidBlockTags;

            var daoid = send.Tags["daoid"];
            var orderid = send.Tags["orderid"];
            var daoblk = await sys.Storage.FindLatestBlockAsync(daoid);
            var orderblk = await sys.Storage.FindLatestBlockAsync(orderid);

            var ordertx = orderblk as TransactionBlock;

            // need some balance to close. old bug
            if (!ordertx.Balances.Any(a => a.Value > 0))
                return APIResultCodes.InsufficientFunds;

            if (daoblk == null || orderblk == null || 
                (orderblk as IUniOrder).Order.daoId != (daoblk as TransactionBlock).AccountID)
                return APIResultCodes.InvalidTrade;

            if ((orderblk as IBrokerAccount).OwnerAccountId != send.AccountID)
                return APIResultCodes.NotSellerOfTrade;

            if ((orderblk as IUniOrder).OOStatus != UniOrderStatus.Open &&
                (orderblk as IUniOrder).OOStatus != UniOrderStatus.Partial &&
                (orderblk as IUniOrder).OOStatus != UniOrderStatus.Delist)
                return APIResultCodes.InvalidOrderStatus;

            var trades = await sys.Storage.FindUniTradeForOrderAsync(orderid);
            if(trades.Any())
            {
                var opened = trades.Cast<IUniTrade>()
                    .Where(a => a.OTStatus != UniTradeStatus.Canceled
                        && a.OTStatus != UniTradeStatus.Closed
                        && a.OTStatus != UniTradeStatus.DisputeClosed
                        && a.OTStatus != UniTradeStatus.PropReleased
                    );
                if(opened.Any())
                {
                    return APIResultCodes.TradesPending;
                }
            }
            return APIResultCodes.Success;
        }

        async Task<TransactionBlock> SealOrderAsync(DagSystem sys, SendTransferBlock send)
        {
            var daoid = send.Tags["daoid"];
            var orderid = send.Tags["orderid"];

            var lastblock = await sys.Storage.FindLatestBlockAsync(orderid) as TransactionBlock;
            var order = (lastblock as IUniOrder).Order;
            var propg = await sys.Storage.FindBlockByHashAsync(order.propHash) as TokenGenesisBlock;

            var sb = await sys.Storage.GetLastServiceBlockAsync();
            var sendToTradeBlock = new UniOrderSendBlock
            {
                // block
                ServiceHash = sb.Hash,

                // trans
                Fee = 0,
                FeeCode = LyraGlobal.OFFICIALTICKERCODE,
                FeeType = AuthorizationFeeTypes.NoFee,
                AccountID = lastblock.AccountID,
                Balances = lastblock.Balances.ToDecimalDict().ToLongDict(),

                // send
                DestinationAccountId = (lastblock as IBrokerAccount).OwnerAccountId,

                // broker
                Name = ((IBrokerAccount)lastblock).Name,
                OwnerAccountId = ((IBrokerAccount)lastblock).OwnerAccountId,
                RelatedTx = send.Hash,

                // Uni
                Order = new UniOrder
                {
                    daoId = ((IUniOrder)lastblock).Order.daoId,
                    dir = ((IUniOrder)lastblock).Order.dir,
                    propType= ((IUniOrder)lastblock).Order.propType,
                    propHash = ((IUniOrder)lastblock).Order.propHash,
                    moneyType = ((IUniOrder)lastblock).Order.moneyType,
                    moneyHash = ((IUniOrder)lastblock).Order.moneyHash,
                    price = ((IUniOrder)lastblock).Order.price,
                    limitMax = ((IUniOrder)lastblock).Order.limitMax,
                    limitMin = ((IUniOrder)lastblock).Order.limitMin,
                    payBy = ((IUniOrder)lastblock).Order.payBy,
                    amount = 0,
                    cltamt = 0,
                },
                OOStatus = UniOrderStatus.Closed,
            };

            if(order.dir == TradeDirection.Sell)
            {
                // when delist, the crypto balance is already zero. 
                // no balance change will vialate the rule of send
                // so we reduce the balance of LYR, or the collateral, of 0.00000001
                if (sendToTradeBlock.Balances[propg.Ticker] != 0)
                    sendToTradeBlock.Balances[propg.Ticker] = 0;
            }
            else
            {
                // the buy order has no crypto at all.
            }
            
            sendToTradeBlock.Balances["LYR"] = 0;          // all remaining LYR

            sendToTradeBlock.AddTag(Block.MANAGEDTAG, WFState.Running.ToString());

            sendToTradeBlock.InitializeBlock(lastblock, NodeService.Dag.PosWallet.PrivateKey, AccountId: NodeService.Dag.PosWallet.AccountId);
            return sendToTradeBlock;
        }

        protected async Task<TransactionBlock> SendCollateralToSellerAsync(DagSystem sys, SendTransferBlock send)
        {
            var daoid = send.Tags["daoid"];
            var orderid = send.Tags["orderid"];
            var orderlatest = await sys.Storage.FindLatestBlockAsync(orderid) as TransactionBlock;
            var daolastblock = await sys.Storage.FindLatestBlockAsync(daoid) as TransactionBlock;

            // get dao for order genesis
            var odrgen = await sys.Storage.FindFirstBlockAsync(orderid) as ReceiveTransferBlock;
            var daoforodr = await sys.Storage.FindBlockByHashAsync(odrgen.SourceHash) as IDao;
            var order = (odrgen as IUniOrder).Order;

            // order owner's fee is calculated on order close.
            // trade owner's fee is calculated on trade close.
            // calculate fees
            // dao fee + network fee
            decimal totalFee = 0;
            decimal networkFee = 0;

            var allTrades = await sys.Storage.FindUniTradeForOrderAsync(orderid);
            var totalAmount = allTrades.Cast<IUniTrade>()
                .Where(a => a.OTStatus == UniTradeStatus.PropReleased)
                .Sum(a => a.Trade.amount);

            //if(order.collateralPrice > 0)       // the price should not be zero. for compatibile only.
            //{
            //    // transaction fee
            //    if (order.dir == TradeDirection.Sell)
            //    {
            //        totalFee += Math.Round((((totalAmount * order.price) * order.fiatPrice) * daoforodr.SellerFeeRatio) / order.collateralPrice, 8);
            //    }
            //    else
            //    {
            //        totalFee += Math.Round((((totalAmount * order.price) * order.fiatPrice) * daoforodr.BuyerFeeRatio) / order.collateralPrice, 8);
            //    }

            //    // network fee
            //    networkFee = Math.Round((((totalAmount * order.price) * order.fiatPrice) * 0.002m) / order.collateralPrice, 8);
            //}
            
            var amountToSeller = order.cltamt - totalFee;

            //Console.WriteLine($"collateral: {order.collateral} txfee: {totalFee} netfee: {networkFee} remains: {order.collateral - totalFee - networkFee} cost: {totalFee + networkFee}");

            var sb = await sys.Storage.GetLastServiceBlockAsync();
            var sendCollateral = new DaoSendBlock
            {
                // block
                ServiceHash = sb.Hash,

                // trans
                Fee = networkFee,
                FeeCode = LyraGlobal.OFFICIALTICKERCODE,
                FeeType = AuthorizationFeeTypes.Dynamic,
                AccountID = daolastblock.AccountID,

                // send
                DestinationAccountId = (orderlatest as IBrokerAccount).OwnerAccountId,

                // broker
                Name = ((IBrokerAccount)daolastblock).Name,
                OwnerAccountId = ((IBrokerAccount)daolastblock).OwnerAccountId,
                RelatedTx = send.Hash,

                // profiting
                PType = ((IProfiting)daolastblock).PType,
                ShareRito = ((IProfiting)daolastblock).ShareRito,
                Seats = ((IProfiting)daolastblock).Seats,

                // dao
                SellerFeeRatio = ((IDao)daolastblock).SellerFeeRatio,
                BuyerFeeRatio = ((IDao)daolastblock).BuyerFeeRatio,
                SellerPar = ((IDao)daolastblock).SellerPar,
                BuyerPar = ((IDao)daolastblock).BuyerPar,
                Description = ((IDao)daolastblock).Description,
                Treasure = ((IDao)daolastblock).Treasure.ToDecimalDict().ToLongDict(),
            };

            // calculate balance
            var dict = daolastblock.Balances.ToDecimalDict();
            dict[LyraGlobal.OFFICIALTICKERCODE] -= amountToSeller;
            sendCollateral.Balances = dict.ToLongDict();

            sendCollateral.AddTag(Block.MANAGEDTAG, WFState.Finished.ToString());

            sendCollateral.InitializeBlock(daolastblock, NodeService.Dag.PosWallet.PrivateKey, AccountId: NodeService.Dag.PosWallet.AccountId);
            return sendCollateral;
        }
    }
}
