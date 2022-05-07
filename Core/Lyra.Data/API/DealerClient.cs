﻿using DexServer.Ext;
using Lyra.Core.API;
using Lyra.Core.Blocks;
using Lyra.Data.Crypto;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lyra.Data.API
{
    public class CommentConfig : SignableObject
    {
        public string ByAccountId { get; set; } = null!;
        public string TradeId { get; set; } = null!;
        public DateTime Created { get; set; }
        public int Rating { get; set; }
        public string Content { get; set; }
        public string Title { get; set; }
        public string EncContent { get; set; }
        public string EncTitle { get; set; }
        public bool Confirm { get; set; }

        public override string GetHashInput()
        {
            if (EncContent == null || EncTitle == null)
                throw new ArgumentNullException();

            return $"{TradeId}|{DateTimeToString(Created)}{Rating}|{ByAccountId}|{EncTitle}|{EncContent}";
        }

        protected override string GetExtraData()
        {
            return "";
        }
    }

    public class FiatInfo
    {
        public string symbol { get; set; }
        public string name { get; set; }
        public string unit { get; set; }
    }
    public class DealerClient : WebApiClientBase
    {
        public DealerClient(string networkid)
        {
            if (networkid == "devnet")
                UrlBase = "https://dealer.devnet.lyra.live:7070/api/Dealer/";
            else if (networkid == "testnet")
                UrlBase = "https://dealertestnet.lyra.live/api/Dealer/";
            else
                UrlBase = "https://dealer.lyra.live/api/Dealer/";
        }

        public async Task<Dictionary<string, decimal>> GetPricesAsync()
        {
            var result = await GetAsync<SimpleJsonAPIResult>("GetPrices");
            if (result.Successful())
                return JsonConvert.DeserializeObject<Dictionary<string, decimal>>(result.JsonString);
            else
                throw new Exception($"Error GetPricesAsync: {result.ResultCode}, {result.ResultMessage}");
        }

        public async Task<FiatInfo> GetFiatAsync(string symbol)
        {
            var args = new Dictionary<string, string>
            {
                { "symbol", symbol },
            };
            var fiat = await GetAsync<SimpleJsonAPIResult>("GetFiat", args);
            if (fiat.Successful() && fiat.JsonString != "null")
                return JsonConvert.DeserializeObject<FiatInfo>(fiat.JsonString);
            
            return null;
        }

        public async Task<SimpleJsonAPIResult> GetUserByAccountIdAsync(string accountId)
        {
            var args = new Dictionary<string, string>
            {
                { "accountId", accountId },
            };
            return await GetAsync<SimpleJsonAPIResult>("GetUserByAccountId", args);
        }

        public async Task<APIResult> RegisterAsync(string accountId,
            string userName, string firstName, string middleName, string lastName,
            string email, string mibilePhone, string avatarId, string signature
            )
        {
            var args = new Dictionary<string, string>
            {
                { "accountId", accountId },
                { "userName", userName },
                { "firstName", firstName },
                { "middleName", middleName },
                { "lastName", lastName },
                { "email", email },
                { "mibilePhone", mibilePhone },
                { "avatarId", avatarId },
                { "signature", signature },
            };
            return await GetAsync<APIResult>("Register", args);
        }

        public async Task<ImageUploadResult> UploadImageAsync(string accountId, string signature, string tradeId, 
            string fileName, byte[] imageData, string contentType)
        {
            var form = new MultipartFormDataContent {
                {
                    new ByteArrayContent(imageData),
                    "file",
                    fileName
                },
                {
                    new StringContent(accountId), "accountId"
                },
                {
                    new StringContent(signature), "signature"
                },
                {
                    new StringContent(tradeId), "tradeId"
                },
            };
            var postResponse = await PostRawAsync<ImageUploadResult>("UploadFile", form);
            return postResponse;
        }

        public async Task<SimpleJsonAPIResult> GetTradeBriefAsync(string tradeId, string accountId, string signature)
        {
            var args = new Dictionary<string, string>
            {
                { "tradeId", tradeId },
                { "accountId", accountId },
                { "signature", signature },
            };
            return await GetAsync<SimpleJsonAPIResult>("GetTradeBrief", args);
        }

        public async Task<APIResult> CommentTrade(CommentConfig cfg)
        {
            return await PostAsync<CommentConfig>("CommentTrade", cfg);
        }
    }
}
