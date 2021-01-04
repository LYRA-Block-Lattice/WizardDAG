﻿using System;
using System.Collections.Generic;
using Lyra.Core.Blocks;
using Lyra.Data.Crypto;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Lyra.Core.Utils;
using Lyra.Core.Accounts;
using System.Diagnostics;
using Lyra.Core.API;

namespace Lyra.Core.Authorizers
{
    public class SendTransferAuthorizer : BaseAuthorizer
    {
        public SendTransferAuthorizer()
        {
        }

        public override async Task<(APIResultCodes, AuthorizationSignature)> AuthorizeAsync<T>(DagSystem sys, T tblock)
        {
            var result = await AuthorizeImplAsync(sys, tblock);
            if (APIResultCodes.Success == result)
                return (APIResultCodes.Success, Sign(sys, tblock));
            else
                return (result, (AuthorizationSignature)null);
        }
        private async Task<APIResultCodes> AuthorizeImplAsync<T>(DagSystem sys, T tblock)
        {
            if (!(tblock is SendTransferBlock))
                return APIResultCodes.InvalidBlockType;

            var block = tblock as SendTransferBlock;

            if (block.AccountID.Equals(block.DestinationAccountId))
                return APIResultCodes.CannotSendToSelf;

            //// 1. check if the account already exists
            //if (!await sys.Storage.AccountExists(block.AccountID))
            //    return APIResultCodes.AccountDoesNotExist;
            //var stopwatch = Stopwatch.StartNew();

            //TransactionBlock lastBlock = null;
            //int count = 50;
            //while(count-- > 0)
            //{
            //    lastBlock = await sys.Storage.FindBlockByHashAsync(block.PreviousHash);
            //    if (lastBlock != null)
            //        break;
            //    Task.Delay(100).Wait();
            //}

            if (await sys.Storage.WasAccountImportedAsync(block.AccountID))
                return APIResultCodes.CannotModifyImportedAccount;

            TransactionBlock lastBlock = await sys.Storage.FindBlockByHashAsync(block.PreviousHash) as TransactionBlock;

            //TransactionBlock lastBlock = await sys.Storage.FindLatestBlock(block.AccountID);
            if (lastBlock == null)
                return APIResultCodes.PreviousBlockNotFound;
            
            var result = await VerifyBlockAsync(sys, block, lastBlock);
            //stopwatch.Stop();
            //Console.WriteLine($"SendTransfer VerifyBlock takes {stopwatch.ElapsedMilliseconds} ms.");

            if (result != APIResultCodes.Success)
                return result;

            //if (lastBlock.Balances[LyraGlobal.LYRA_TICKER_CODE] <= block.Balances[LyraGlobal.LYRA_TICKER_CODE] + block.Fee)
            //    return AuthorizationResultCodes.NegativeTransactionAmount;

            // Validate the destination account id
            if (!Signatures.ValidateAccountId(block.DestinationAccountId))
                return APIResultCodes.InvalidDestinationAccountId;

            //var stopwatch2 = Stopwatch.StartNew();
            result = await VerifyTransactionBlockAsync(sys, block);
            //stopwatch2.Stop();
            //Console.WriteLine($"SendTransfer VerifyTransactionBlock takes {stopwatch2.ElapsedMilliseconds} ms.");
            if (result != APIResultCodes.Success)
                return result;

            //var stopwatch3 = Stopwatch.StartNew();
            if (!block.ValidateTransaction(lastBlock))
                return APIResultCodes.SendTransactionValidationFailed;

            result = await ValidateNonFungibleAsync(sys, block, lastBlock);
            if (result != APIResultCodes.Success)
                return result;

            //stopwatch3.Stop();
            //Console.WriteLine($"SendTransfer ValidateTransaction & ValidateNonFungible takes {stopwatch3.ElapsedMilliseconds} ms.");

            // a normal send is success.
            // monitor special account
            if (block.DestinationAccountId == PoolFactoryBlock.FactoryAccount)
            {
                if (block.Tags != null
                    && block.Tags.ContainsKey(Block.REQSERVICETAG)
                    && block.Tags.ContainsKey("token0") && await CheckTokenAsync(sys, block.Tags["token0"])
                    && block.Tags.ContainsKey("token1") && await CheckTokenAsync(sys, block.Tags["token1"])
                    && block.Tags["token0"] != block.Tags["token1"]
                    && (block.Tags["token0"] == LyraGlobal.OFFICIALTICKERCODE || block.Tags["token1"] == LyraGlobal.OFFICIALTICKERCODE)
                    )
                {
                    var chgs = block.GetBalanceChanges(lastBlock);
                    if (!chgs.Changes.ContainsKey(LyraGlobal.OFFICIALTICKERCODE))
                        return APIResultCodes.InvalidFeeAmount;

                    if(chgs.Changes.Count > 1)
                        return APIResultCodes.InvalidFeeAmount;

                    if (block.Tags[Block.REQSERVICETAG] == "")
                    {
                        if (chgs.Changes[LyraGlobal.OFFICIALTICKERCODE] != PoolFactoryBlock.PoolCreateFee)
                            return APIResultCodes.InvalidFeeAmount;

                        // check if pool exists
                        var factory = await sys.Storage.GetPoolFactoryAsync();
                        if (factory == null)
                            return APIResultCodes.SystemNotReadyToServe;

                        var poolGenesis = await sys.Storage.GetPoolAsync(block.Tags["token0"], block.Tags["token1"]);
                        if (poolGenesis != null)
                            return APIResultCodes.PoolAlreadyExists;
                    }

                    if (block.Tags[Block.REQSERVICETAG] == "poolwithdraw")
                    {
                        if (chgs.Changes[LyraGlobal.OFFICIALTICKERCODE] != 1m)
                            return APIResultCodes.InvalidFeeAmount;

                        var poolGenesis = await sys.Storage.GetPoolAsync(block.Tags["token0"], block.Tags["token1"]);
                        if (poolGenesis == null)
                            return APIResultCodes.PoolNotExists;

                        var pool = await sys.Storage.FindLatestBlockAsync(poolGenesis.AccountID) as IPool;
                        if(pool == null)
                            return APIResultCodes.PoolNotExists;

                        if (!pool.Shares.ContainsKey(block.AccountID))
                            return APIResultCodes.PoolShareNotExists;
                    }

                    return APIResultCodes.Success;
                }
                else
                    return APIResultCodes.InvalidTokenPair;
            }

            return APIResultCodes.Success;
        }

        protected override async Task<APIResultCodes> ValidateFeeAsync(DagSystem sys, TransactionBlock block)
        {
            APIResultCodes result = APIResultCodes.Success;
            if (block.FeeType != AuthorizationFeeTypes.Regular)
                result = APIResultCodes.InvalidFeeAmount;

            if (block.Fee != (await sys.Storage.GetLastServiceBlockAsync()).TransferFee)
                result = APIResultCodes.InvalidFeeAmount;

            return result;
        }

        protected override async Task<APIResultCodes> ValidateCollectibleNFTAsync(DagSystem sys, TransactionBlock send_or_receice_block, TokenGenesisBlock token_block)
        {
            if (send_or_receice_block.NonFungibleToken.Denomination != 1)
                return APIResultCodes.InvalidCollectibleNFTDenomination;

            if (string.IsNullOrEmpty(send_or_receice_block.NonFungibleToken.SerialNumber))
                return APIResultCodes.InvalidCollectibleNFTSerialNumber;

            bool nft_instance_exists = await WasNFTInstanceIssuedAsync(sys, token_block, send_or_receice_block.NonFungibleToken.SerialNumber);
            bool is_there_a_token = await sys.Storage.DoesAccountHaveCollectibleNFTInstanceAsync(send_or_receice_block.AccountID, token_block, send_or_receice_block.NonFungibleToken.SerialNumber);

            if (nft_instance_exists) // this is a transfer of existing instance to another account
            {
                if (!is_there_a_token && send_or_receice_block.AccountID == token_block.AccountID)
                    return APIResultCodes.DuplicateNFTCollectibleSerialNumber;

                if (!is_there_a_token && send_or_receice_block.AccountID != token_block.AccountID)
                    return APIResultCodes.InsufficientFunds;
            }
            else // otherwise, this is an attempt to issue a new instance
            {
                // Only the owner of the genesis token block can issue a new instance of NFT
                if (send_or_receice_block.AccountID != token_block.AccountID)
                    return APIResultCodes.NFTCollectibleSerialNumberDoesNotExist;
            }

            return APIResultCodes.Success;
        }

    }
}