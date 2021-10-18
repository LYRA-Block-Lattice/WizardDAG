﻿using Lyra.Core.Blocks;
using Lyra.Data.Crypto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lyra.Core.Authorizers
{
    public class ProfitingGenesisAuthorizer : BaseAuthorizer
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
            if (!(tblock is ProfitingGenesisBlock))
                return APIResultCodes.InvalidBlockType;

            var block = tblock as ProfitingGenesisBlock;

            // Validate blocks
            var result = await VerifyBlockAsync(sys, block, null);
            if (result != APIResultCodes.Success)
                return result;

            // first verify account id
            var pf = await sys.Storage.GetPoolFactoryAsync();

            // create a semi random account for pool.
            // it can be verified by other nodes.
            var keyStr = $"{pf.Height},{block.PType},{block.ShareRito},{block.Seats},{pf.Hash}";
            var (_, AccountId) = Signatures.GenerateWallet(Encoding.ASCII.GetBytes(keyStr).Take(32).ToArray());

            if (block.AccountID != AccountId)
                return APIResultCodes.InvalidAccountId;

            return APIResultCodes.Success;
        }
    }
}
