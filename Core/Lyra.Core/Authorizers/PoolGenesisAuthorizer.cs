﻿using Lyra.Core.Blocks;
using Lyra.Data.Crypto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lyra.Core.Authorizers
{
    public class PoolGenesisAuthorizer : ReceiveTransferAuthorizer
    {
        protected override async Task<APIResultCodes> AuthorizeImplAsync<T>(DagSystem sys, T tblock)
        {
            if (!(tblock is PoolGenesisBlock))
                return APIResultCodes.InvalidBlockType;

            var block = tblock as PoolGenesisBlock;

            // related tx must exist 
            var relTx = await sys.Storage.FindBlockByHashAsync(block.RelatedTx) as SendTransferBlock;
            if (relTx == null || relTx.DestinationAccountId != PoolFactoryBlock.FactoryAccount)
                return APIResultCodes.InvalidServiceRequest;

            // service must not been processed
            var processed = await sys.Storage.FindBlocksByRelatedTxAsync(block.RelatedTx);
            if (processed.Count != 0)
                return APIResultCodes.InvalidServiceRequest;

            // create a semi random account for pool.
            // it can be verified by other nodes.
            var keyStr = $"{relTx.Hash.Substring(0, 16)},{block.Token0},{block.Token1},{relTx.AccountID}";
            var AccountId = Base58Encoding.EncodeAccountId(Encoding.ASCII.GetBytes(keyStr).Take(64).ToArray());

            if (block.AccountID != AccountId)
                return APIResultCodes.InvalidAccountId;

            return await base.AuthorizeImplAsync(sys, tblock);
        }
    }
}
