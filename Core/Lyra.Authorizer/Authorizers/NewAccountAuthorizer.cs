﻿using Lyra.Core.Blocks.Transactions;

using Lyra.Core.API;
using Lyra.Core.Accounts.Node;
using Lyra.Authorizer.Services;
using Lyra.Authorizer.Decentralize;
using System.Threading.Tasks;
using Lyra.Core.Blocks;
using Microsoft.Extensions.Options;
using Lyra.Core.Cryptography;

namespace Lyra.Authorizer.Authorizers
{
    public class NewAccountAuthorizer: ReceiveTransferAuthorizer
    {
        public NewAccountAuthorizer(IOptions<LyraConfig> config, ServiceAccount serviceAccount, IAccountCollection accountCollection)
            : base(config, serviceAccount, accountCollection)
        {
        }

        public override Task<APIResultCodes> Authorize<T>(T tblock)
        {
            return AuthorizeImplAsync<T>(tblock);
        }
        private async Task<APIResultCodes> AuthorizeImplAsync<T>(T tblock)
        {
            if (!(tblock is OpenWithReceiveTransferBlock))
                return APIResultCodes.InvalidBlockType;

            var block = tblock as OpenWithReceiveTransferBlock;

            // 1. check if the account already exists
            if (_accountCollection.AccountExists(block.AccountID))
                return APIResultCodes.AccountAlreadyExists;

            // This is redundant but just in case
            if (_accountCollection.FindLatestBlock(block.AccountID) != null)
                return APIResultCodes.AccountBlockAlreadyExists;

            var result = await VerifyBlockAsync(block, null);
            if (result != APIResultCodes.Success)
                return result;

            result = await VerifyTransactionBlockAsync(block);
            if (result != APIResultCodes.Success)
                return result;

            result = ValidateReceiveTransAmount(block as ReceiveTransferBlock, block.GetTransaction(null));
            if (result != APIResultCodes.Success)
                return result;

            result = await ValidateNonFungibleAsync(block, null);
            if (result != APIResultCodes.Success)
                return result;

            var signed = await Sign(block);
            if (signed)
            {
                _accountCollection.AddBlock(block);
                return APIResultCodes.Success;
            }
            else
            {
                return APIResultCodes.NotAllowedToSign;
            }
        }
    }
}
