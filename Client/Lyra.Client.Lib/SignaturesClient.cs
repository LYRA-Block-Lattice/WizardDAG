using Lyra.Core.Cryptography;
using System;
using System.Text;
using System.Threading.Tasks;

namespace Lyra.Client.Lib
{
    //
    // Parts of this code are from https://github.com/sander-/working-with-digital-signatures
    //
    public class SignaturesClient : ISignatures
    {
        public bool ValidateAccountId(string AccountId)
        {
            return SignaturesBase.ValidateAccountId(AccountId);
        }

        // It can validate either public or private key - thanks to the checksum
        public bool ValidatePublicKey(string PublicKey)
        {
            return SignaturesBase.ValidatePublicKey(PublicKey);
        }

        public bool ValidatePrivateKey(string PrivateKey)
        {
            return SignaturesBase.ValidatePrivateKey(PrivateKey);
        }

        public Task<bool> VerifyAccountSignature(string message, string accountId, string signature)
        {
            return Task.FromResult(SignaturesBase.VerifyAccountSignature(message, accountId, signature));
        }

        public Task<bool> VerifyAuthorizerSignature(string message, string publicKey, string signature)
        {
            return Task.FromResult(SignaturesBase.VerifyAuthorizerSignature(message, publicKey, signature));
        }

        public Task<string> GetSignature(string privateKey, string message)
        {
            return Task.FromResult(SignaturesBase.GetSignature(privateKey, message));
        }

        public Task<string> GetAccountIdFromPrivateKey(string privateKey)
        {
            return Task.FromResult(SignaturesBase.GetAccountIdFromPrivateKey(privateKey));
        }

        public Task<string> GetPublicKeyFromPrivateKey(string privateKey)
        {
            return Task.FromResult(SignaturesBase.GetPublicKeyFromPrivateKey(privateKey));
        }

        public Task<(string privateKey, string publicKey)> GenerateWallet()
        {
            return Task.FromResult(SignaturesBase.GenerateWallet());
        }
    }
}
