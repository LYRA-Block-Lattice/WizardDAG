﻿using Lyra.Core.Blocks;
using Lyra.Data.Blocks;
using Lyra.Data.Crypto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lyra.Core.Authorizers
{
    public class BenefitingAuthorizer : BaseAuthorizer
    {
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
            if (!(tblock is BenefitingBlock))
                return APIResultCodes.InvalidBlockType;

            var block = tblock as BenefitingBlock;

            // Validate blocks
            Block prev = null;
            if (block.Height > 1)
                prev = await sys.Storage.FindBlockByHashAsync(block.PreviousHash);
            var result = await VerifyBlockAsync(sys, block, prev);
            if (result != APIResultCodes.Success)
                return result;

            // related tx must exist 
            var relTx = await sys.Storage.FindBlockByHashAsync(block.RelatedTx) as SendTransferBlock;
            if (relTx == null)
                return APIResultCodes.InvalidServiceRequest;

            //// service must not been processed
            //var processed = await sys.Storage.FindBlocksByRelatedTxAsync(block.RelatedTx);
            //if (processed.Count != 0)
            //    return APIResultCodes.InvalidServiceRequest;

            return APIResultCodes.Success;
        }
    }
}