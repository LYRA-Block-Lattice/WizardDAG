﻿using Converto;
using Lyra.Core.API;
using Lyra.Core.Authorizers;
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
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lyra.Core.WorkFlow
{
    [LyraWorkFlow]//v
    public class WFUniTradeCreate : WorkFlowBase
    {
        public override WorkFlowDescription GetDescription()
        {
            return new WorkFlowDescription
            {
                Action = BrokerActions.BRK_UNI_CRTRD,
                RecvVia = BrokerRecvType.DaoRecv,
            };
        }

        public override Task<Func<DagSystem, SendTransferBlock, Task<TransactionBlock>>[]> GetProceduresAsync(DagSystem sys, SendTransferBlock send)
        {
            if (send.Tags == null)
                throw new ArgumentNullException();

            var trade = JsonConvert.DeserializeObject<UniTrade>(send.Tags["data"]);
            if(trade == null)
                throw new ArgumentNullException();

            if (trade.dir == TradeDirection.Buy)
            {
                return Task.FromResult(new[] {
                    SendTokenFromOrderToTradeAsync,
                    TradeGenesisReceiveAsync });
            }
            else
            {
                return Task.FromResult(new[] {
                    SendTokenFromDaoToOrderAsync,
                    OrderReceiveCryptoAsync,
                    SendTokenFromOrderToTradeAsync,
                    TradeGenesisReceiveAsync
                });
            }
        }

        public override async Task<APIResultCodes> PreSendAuthAsync(DagSystem sys, SendTransferBlock send, TransactionBlock last)
        {
            if (send.Tags.Count != 2 ||
                !send.Tags.ContainsKey("data") ||
                string.IsNullOrWhiteSpace(send.Tags["data"])
                )
                return APIResultCodes.InvalidBlockTags;

            UniTrade trade;
            try
            {
                trade = JsonConvert.DeserializeObject<UniTrade>(send.Tags["data"]);
            }
            catch (Exception ex)
            {
                return APIResultCodes.InvalidBlockTags;
            }

            // daoId
            var dao = await sys.Storage.FindLatestBlockAsync(trade.daoId);
            if (dao == null || (dao as TransactionBlock).AccountID != send.DestinationAccountId)
                return APIResultCodes.InvalidOrgnization;

            // orderId
            var orderblk = await sys.Storage.FindLatestBlockAsync(trade.orderId) as IUniOrder;
            if (orderblk == null)
                return APIResultCodes.InvalidOrder;

            var order = orderblk.Order;
            if (string.IsNullOrWhiteSpace(order.dealerId))
                return APIResultCodes.InvalidOrder;

            if (order.daoId != trade.daoId ||
                order.dealerId != trade.dealerId ||
                order.propHash != trade.propHash ||
                order.moneyHash != trade.moneyHash ||
                order.price != trade.price ||
                order.amount < trade.amount ||
                order.dir == trade.dir ||
                orderblk.OwnerAccountId != trade.orderOwnerId ||
                trade.pay > order.limitMax ||
                trade.pay < order.limitMin ||
                !order.payBy.Contains(trade.payVia)
                )
                return APIResultCodes.InvalidTrade;

            // pay
            if(trade.pay != Math.Round(trade.pay, 2))
                return APIResultCodes.InvalidTradeAmount;

            var got = Math.Round(trade.pay / order.price, 8);
            if(got != trade.amount)
                return APIResultCodes.InvalidTradeAmount;

            // verify collateral
            var chgs = send.GetBalanceChanges(last);
            if (!chgs.Changes.ContainsKey(LyraGlobal.OFFICIALTICKERCODE) ||
                chgs.Changes[LyraGlobal.OFFICIALTICKERCODE] < trade.cltamt)
                return APIResultCodes.InvalidCollateral;

            var propg = sys.Storage.FindBlockByHash(trade.propHash) as TokenGenesisBlock;
            if(trade.dir == TradeDirection.Sell)
            {
                if(!chgs.Changes.ContainsKey(propg.Ticker) ||
                    chgs.Changes[propg.Ticker] != trade.amount ||
                        chgs.Changes.Count != 2)
                {
                    return APIResultCodes.InvalidAmountToSend;
                }
            }
            else
            {
                if (chgs.Changes.Count != 1)
                    return APIResultCodes.InvalidAmountToSend;
            }

            // check the price of order and collateral.
            var dlrblk = await sys.Storage.FindLatestBlockAsync(trade.dealerId);
            var uri = new Uri(new Uri((dlrblk as IDealer).Endpoint), "/api/dealer/");
            var dealer = new DealerClient(uri);
            var prices = await dealer.GetPricesAsync();
            var tokenSymbol = propg.Ticker.Split('/')[1];

            if(prices.ContainsKey(tokenSymbol))
            {
                // only calculate the worth of collateral when we have a standard price for the property.
                if (trade.dir == TradeDirection.Buy)
                {
                    if (trade.cltamt * prices["LYR"] < prices[tokenSymbol] * trade.amount * ((dao as IDao).BuyerPar / 100))
                        return APIResultCodes.CollateralNotEnough;
                }
                else
                {
                    if (trade.cltamt * prices["LYR"] < prices[tokenSymbol] * trade.amount  * ((dao as IDao).SellerPar / 100))
                        return APIResultCodes.CollateralNotEnough;
                }
            }

            return APIResultCodes.Success;
        }

        async Task<TransactionBlock> SendTokenFromOrderToTradeAsync(DagSystem sys, SendTransferBlock send)
        {
            var trade = JsonConvert.DeserializeObject<UniTrade>(send.Tags["data"]);

            // send token from order to trade
            var lastblock = await sys.Storage.FindLatestBlockAsync(trade.orderId) as TransactionBlock;
            var propg = sys.Storage.FindBlockByHash(trade.propHash) as TokenGenesisBlock;

            var keyStr = $"{send.Hash.Substring(0, 16)},{propg.Ticker},{send.AccountID}";
            var AccountId = Base58Encoding.EncodeAccountId(Encoding.ASCII.GetBytes(keyStr).Take(64).ToArray());

            return await TransactionOperateAsync(sys, send.Hash, lastblock,
                () => lastblock.GenInc<UniOrderSendBlock>(),
                () => WFState.Running,
                (b) =>
                {
                    // send
                    (b as SendTransferBlock).DestinationAccountId = AccountId;

                    // IUniTrade
                    var nextOdr = b as IUniOrder;
                    nextOdr.Order = nextOdr.Order.With(new
                    {
                        amount = ((IUniOrder)lastblock).Order.amount - trade.amount,
                    });
                    nextOdr.OOStatus = ((IUniOrder)lastblock).Order.amount - trade.amount == 0 ?
                        UniOrderStatus.Closed : UniOrderStatus.Partial;

                    // calculate balance
                    var dict = lastblock.Balances.ToDecimalDict();
                    dict[propg.Ticker] -= trade.amount;
                    b.Balances = dict.ToLongDict();
                });

            //var nextblock = lastblock.GenInc<UniOrderSendBlock>();  //gender change
            //var sendtotrade = nextblock
            //    .With(new
            //    {
            //            // generic
            //        ServiceHash = sb.Hash,
            //        BlockType = BlockTypes.UniOrderSend,

            //            // send & recv
            //        DestinationAccountId = AccountId,

            //            // broker
            //        RelatedTx = send.Hash,

            //            // business object
            //        Order = nextblock.Order.With(new
            //        {
            //            amount = ((IUniOrder)lastblock).Order.amount - trade.amount,
            //        }),
            //        OOStatus = ((IUniOrder)lastblock).Order.amount - trade.amount == 0 ?
            //            UniOrderStatus.Closed : UniOrderStatus.Partial,
            //    });

            //// calculate balance
            //var dict = lastblock.Balances.ToDecimalDict();
            //dict[trade.crypto] -= trade.amount;
            //sendtotrade.Balances = dict.ToLongDict();
            //sendtotrade.InitializeBlock(lastblock, NodeService.Dag.PosWallet.PrivateKey, AccountId: NodeService.Dag.PosWallet.AccountId);
            //return sendtotrade;
        }

        async Task<TransactionBlock> SendTokenFromDaoToOrderAsync(DagSystem sys, SendTransferBlock send)
        {
            var trade = JsonConvert.DeserializeObject<UniTrade>(send.Tags["data"]);

            var lastblock = await sys.Storage.FindLatestBlockAsync(trade.daoId) as TransactionBlock;
            var propg = sys.Storage.FindBlockByHash(trade.propHash) as TokenGenesisBlock;

            return await TransactionOperateAsync(sys, send.Hash, lastblock,
                () => lastblock.GenInc<DaoSendBlock>(),
                () => WFState.Running,
                (b) =>
                {
                    // send
                    (b as SendTransferBlock).DestinationAccountId = trade.orderId;

                    // send the amount of crypto from dao to order
                    var dict = lastblock.Balances.ToDecimalDict();
                    dict[propg.Ticker] -= trade.amount;
                    b.Balances = dict.ToLongDict();
                });
        }

        async Task<TransactionBlock> OrderReceiveCryptoAsync(DagSystem sys, SendTransferBlock send)
        {
            var trade = JsonConvert.DeserializeObject<UniTrade>(send.Tags["data"]);
            var propg = sys.Storage.FindBlockByHash(trade.propHash) as TokenGenesisBlock;
            var lastblock = await sys.Storage.FindLatestBlockAsync(trade.orderId) as TransactionBlock;
            var blocks = await sys.Storage.FindBlocksByRelatedTxAsync(send.Hash);

            return await TransactionOperateAsync(sys, send.Hash, lastblock,
                () => lastblock.GenInc<UniOrderRecvBlock>(),
                () => WFState.Running,
                (b) =>
                {
                    // send
                    (b as ReceiveTransferBlock).SourceHash = blocks.Last().Hash;

                    // send the amount of crypto from dao to order
                    var dict = lastblock.Balances.ToDecimalDict();
                    if (dict.ContainsKey(propg.Ticker))
                        dict[propg.Ticker] += trade.amount;
                    else
                        dict.Add(propg.Ticker, trade.amount);
                    b.Balances = dict.ToLongDict();
                });
        }

        async Task<TransactionBlock> TradeGenesisReceiveAsync(DagSystem sys, SendTransferBlock send)
        {
            var blocks = await sys.Storage.FindBlocksByRelatedTxAsync(send.Hash);

            var trade = JsonConvert.DeserializeObject<UniTrade>(send.Tags["data"]);
            var propg = sys.Storage.FindBlockByHash(trade.propHash) as TokenGenesisBlock;
            var keyStr = $"{send.Hash.Substring(0, 16)},{propg.Ticker},{send.AccountID}";
            var AccountId = Base58Encoding.EncodeAccountId(Encoding.ASCII.GetBytes(keyStr).Take(64).ToArray());

            var sb = await sys.Storage.GetLastServiceBlockAsync();
            var Uniblock = new UniTradeGenesisBlock
            {
                ServiceHash = sb.Hash,
                Fee = 0,
                FeeCode = LyraGlobal.OFFICIALTICKERCODE,
                FeeType = AuthorizationFeeTypes.NoFee,

                // transaction
                AccountType = LyraGlobal.GetAccountTypeFromTicker(propg.Ticker, trade.dir),
                AccountID = AccountId,
                Balances = new Dictionary<string, long>(),

                // receive
                SourceHash = (blocks.Last() as TransactionBlock).Hash,

                // broker
                Name = "no name",
                OwnerAccountId = send.AccountID,
                RelatedTx = send.Hash,

                // Uni
                Trade = trade,
            };

            Uniblock.Balances.Add(propg.Ticker, trade.amount.ToBalanceLong());
            Uniblock.AddTag(Block.MANAGEDTAG, WFState.Finished.ToString());

            // pool blocks are service block so all service block signed by leader node
            Uniblock.InitializeBlock(null, NodeService.Dag.PosWallet.PrivateKey, AccountId: NodeService.Dag.PosWallet.AccountId);
            return Uniblock;
        }

        //async Task<TransactionBlock> SendUtilityTokenToUserAsync(DagSystem sys, SendTransferBlock send)
        //{
        //    throw new NotImplementedException();
        //}
    }
}
