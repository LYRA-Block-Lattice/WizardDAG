﻿using Lyra.Core.API;
using Lyra.Core.Blocks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lyra.Data.API
{
    /// <summary>
    /// access all primary nodes to determinate state
    /// </summary>
    public class LyraAggregatedClient : INodeAPI, INodeTransactionAPI, INodeDexAPI
    {
        private string _networkId;
        private LyraRestClient _seedClient;
        private Dictionary<string, LyraRestClient> _primaryClients;

        public LyraRestClient SeedClient { get => _seedClient; set => _seedClient = value; }

        public LyraAggregatedClient(string networkId)
        {
            this._networkId = networkId;
        }

        public async Task InitAsync(string seedNodeAddress)
        {
            var platform = Environment.OSVersion.Platform.ToString();
            var appName = "LyraAggregatedClient";
            var appVer = "1.0";

            ushort peerPort = 4504;
            if (_networkId == "mainnet")
                peerPort = 5504;

            // get latest service block
            SeedClient = LyraRestClient.Create(_networkId, platform, appName, appVer, $"https://{seedNodeAddress}:{peerPort}/api/Node/");

            // get nodes list (from billboard)
            var seedBillBoard = await SeedClient.GetBillBoardAsync();

            // create clients for primary nodes
            _primaryClients = seedBillBoard.NodeAddresses
                .Where(a => seedBillBoard.PrimaryAuthorizers.Contains(a.Key))
                .Select(c => new
                {
                    c.Key,
                    Value = LyraRestClient.Create(_networkId, platform, appName, appVer, $"https://{c.Value}:{peerPort}/api/Node/")
                })
                .ToDictionary(p => p.Key, p => p.Value);
        }

        public async Task<T> CheckResultAsync<T>(List<Task<T>> tasks) where T: APIResult, new()
        {
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception e)
            {
                // do nothing?                
            }

            var goodResults = tasks.Where(a => a.IsCompletedSuccessfully)
                .Select(a => a.Result)
                .Where(a => a.ResultCode == APIResultCodes.Success);

            var goodCount = goodResults.Count();
            if (goodCount >= LyraGlobal.GetMajority(_primaryClients.Count))
            {
                var best = goodResults
                    .GroupBy(b => b)
                    .Select(g => new
                    {
                        Data = g.Key,
                        Count = g.Count()
                    })
                    .OrderByDescending(x => x.Count)
                    .First();

                if (best.Count >= LyraGlobal.GetMajority(_primaryClients.Count))
                {
                    var x = goodResults.First(a => a == best.Data);
                    return x;
                }
                else
                {
                    // only for debug
                    var q = goodResults.Select(a => a.GetHashCode()).ToList();
                    var q2 = goodResults.Select(a => (a as GetSyncStateAPIResult).Status.GetHashCode()).ToList();
                    var q3 = goodResults.Select(a => (a as GetSyncStateAPIResult)).ToList();
                    var q4 = q3;
                }
            }

            return new T { ResultCode = APIResultCodes.APIRouteFailed };
        }

        public async Task<APIResult> CancelExchangeOrder(string AccountId, string Signature, string cancelKey)
        {
            return await SeedClient.CancelExchangeOrder(AccountId, Signature, cancelKey);
        }

        public async Task<AuthorizationAPIResult> CancelTradeOrder(CancelTradeOrderBlock block)
        {
            return await SeedClient.CancelTradeOrder(block);
        }

        public async Task<ExchangeAccountAPIResult> CloseExchangeAccount(string AccountId, string Signature)
        {
            return await SeedClient.CloseExchangeAccount(AccountId, Signature);
        }

        public async Task<ExchangeAccountAPIResult> CreateExchangeAccount(string AccountId, string Signature)
        {
            return await SeedClient.CreateExchangeAccount(AccountId, Signature);
        }

        public async Task<AuthorizationAPIResult> CreateToken(TokenGenesisBlock block)
        {
            return await SeedClient.CreateToken(block);
        }

        public async Task<AuthorizationAPIResult> ExecuteTradeOrder(ExecuteTradeOrderBlock block)
        {
            return await SeedClient.ExecuteTradeOrder(block);
        }

        public async Task<AccountHeightAPIResult> GetAccountHeight(string AccountId)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetAccountHeight(AccountId)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<ActiveTradeOrdersAPIResult> GetActiveTradeOrders(string AccountId, string SellToken, string BuyToken, TradeOrderListTypes OrderType, string Signature)
        {
            return await SeedClient.GetActiveTradeOrders(AccountId, SellToken, BuyToken, OrderType, Signature);
        }

        public async Task<BillBoard> GetBillBoardAsync()
        {
            return await SeedClient.GetBillBoardAsync();
        }

        public async Task<BlockAPIResult> GetBlock(string Hash)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetBlock(Hash)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<BlockAPIResult> GetBlockByHash(string AccountId, string Hash, string Signature)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetBlockByHash(AccountId, Hash, Signature)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<BlockAPIResult> GetBlockByIndex(string AccountId, long Index)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetBlockByIndex(AccountId, Index)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<BlockAPIResult> GetBlockBySourceHash(string sourceHash)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetBlockBySourceHash(sourceHash)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<GetListStringAPIResult> GetBlockHashesByTimeRange(DateTime startTime, DateTime endTime)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetBlockHashesByTimeRange(startTime, endTime)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<GetListStringAPIResult> GetBlockHashesByTimeRange(long startTimeTicks, long endTimeTicks)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetBlockHashesByTimeRange(startTimeTicks, endTimeTicks)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<MultiBlockAPIResult> GetBlocksByConsolidation(string AccountId, string Signature, string consolidationHash)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetBlocksByConsolidation(AccountId, Signature, consolidationHash)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<MultiBlockAPIResult> GetBlocksByTimeRange(DateTime startTime, DateTime endTime)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetBlocksByTimeRange(startTime, endTime)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<MultiBlockAPIResult> GetBlocksByTimeRange(long startTimeTicks, long endTimeTicks)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetBlocksByTimeRange(startTimeTicks, endTimeTicks)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<MultiBlockAPIResult> GetConsolidationBlocks(string AccountId, string Signature, long startHeight, int count)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetConsolidationBlocks(AccountId, Signature, startHeight, count)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<string> GetDbStats()
        {
            return await SeedClient.GetDbStats();
        }

        public async Task<ExchangeBalanceAPIResult> GetExchangeBalance(string AccountId, string Signature)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetExchangeBalance(AccountId, Signature)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<BlockAPIResult> GetLastBlock(string AccountId)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetLastBlock(AccountId)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<BlockAPIResult> GetLastConsolidationBlock()
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetLastConsolidationBlock()).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<BlockAPIResult> GetLastServiceBlock()
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetLastServiceBlock()).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<BlockAPIResult> GetLyraTokenGenesisBlock()
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetLyraTokenGenesisBlock()).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<NonFungibleListAPIResult> GetNonFungibleTokens(string AccountId, string Signature)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetNonFungibleTokens(AccountId, Signature)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<List<ExchangeOrder>> GetOrdersForAccount(string AccountId, string Signature)
        {
            return await SeedClient.GetOrdersForAccount(AccountId, Signature);
        }

        public async Task<BlockAPIResult> GetServiceBlockByIndex(string blockType, long Index)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetServiceBlockByIndex(blockType, Index)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<BlockAPIResult> GetServiceGenesisBlock()
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetServiceGenesisBlock()).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<AccountHeightAPIResult> GetSyncHeight()
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetSyncHeight()).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<GetSyncStateAPIResult> GetSyncState()
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetSyncState()).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<BlockAPIResult> GetTokenGenesisBlock(string AccountId, string TokenTicker, string Signature)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetTokenGenesisBlock(AccountId, TokenTicker, Signature)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<GetListStringAPIResult> GetTokenNames(string AccountId, string Signature, string keyword)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetTokenNames(AccountId, Signature, keyword)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<List<TransStats>> GetTransStatsAsync()
        {
            return await SeedClient.GetTransStatsAsync();
        }

        public async Task<GetVersionAPIResult> GetVersion(int apiVersion, string appName, string appVersion)
        {
            var tasks = _primaryClients.Select(async client => await client.Value.GetVersion(apiVersion, appName, appVersion)).ToList();

            return await CheckResultAsync(tasks);
        }

        public async Task<AuthorizationAPIResult> ImportAccount(ImportAccountBlock block)
        {
            return await SeedClient.ImportAccount(block);
        }

        public async Task<NewFeesAPIResult> LookForNewFees(string AccountId, string Signature)
        {
            return await SeedClient.LookForNewFees(AccountId, Signature);
        }

        public async Task<TradeAPIResult> LookForNewTrade(string AccountId, string BuyTokenCode, string SellTokenCode, string Signature)
        {
            return await SeedClient.LookForNewTrade(AccountId, BuyTokenCode, SellTokenCode, Signature);
        }

        public async Task<NewTransferAPIResult> LookForNewTransfer(string AccountId, string Signature)
        {
            return await SeedClient.LookForNewTransfer(AccountId, Signature);
        }

        public async Task<AuthorizationAPIResult> OpenAccountWithGenesis(LyraTokenGenesisBlock block)
        {
            return await SeedClient.OpenAccountWithGenesis(block);
        }

        public async Task<AuthorizationAPIResult> OpenAccountWithImport(OpenAccountWithImportBlock block)
        {
            return await SeedClient.OpenAccountWithImport(block);
        }

        public async Task<AuthorizationAPIResult> ReceiveFee(ReceiveAuthorizerFeeBlock block)
        {
            return await SeedClient.ReceiveFee(block);
        }

        public async Task<AuthorizationAPIResult> ReceiveTransfer(ReceiveTransferBlock block)
        {
            return await SeedClient.ReceiveTransfer(block);
        }

        public async Task<AuthorizationAPIResult> ReceiveTransferAndOpenAccount(OpenWithReceiveTransferBlock block)
        {
            return await SeedClient.ReceiveTransferAndOpenAccount(block);
        }

        public async Task<APIResult> RequestMarket(string tokenName)
        {
            return await SeedClient.RequestMarket(tokenName);
        }

        public async Task<TransactionsAPIResult> SearchTransactions(string accountId, long startTimeTicks, long endTimeTicks, int count)
        {
            return await SeedClient.SearchTransactions(accountId, startTimeTicks, endTimeTicks, count);
        }

        public async Task<AuthorizationAPIResult> SendExchangeTransfer(ExchangingBlock block)
        {
            return await SeedClient.SendExchangeTransfer(block);
        }

        public async Task<AuthorizationAPIResult> SendTransfer(SendTransferBlock block)
        {
            return await SeedClient.SendTransfer(block);
        }

        public async Task<CancelKey> SubmitExchangeOrder(TokenTradeOrder order)
        {
            return await SeedClient.SubmitExchangeOrder(order);
        }

        public async Task<AuthorizationAPIResult> Trade(TradeBlock block)
        {
            return await SeedClient.Trade(block);
        }

        public async Task<TradeOrderAuthorizationAPIResult> TradeOrder(TradeOrderBlock block)
        {
            return await SeedClient.TradeOrder(block);
        }

        public async Task<List<Voter>> GetVoters(VoteQueryModel model)
        {
            return await SeedClient.GetVotersAsync(model);
        }

        public async Task<List<Vote>> FindVotes(VoteQueryModel model)
        {
            return await SeedClient.FindVotesAsync(model);
        }

        public async Task<FeeStats> GetFeeStats()
        {
            return await SeedClient.GetFeeStatsAsync();
        }

        // because using class not interface so these not used
        List<Voter> INodeAPI.GetVoters(VoteQueryModel model)
        {
            throw new NotImplementedException();
        }

        List<Vote> INodeAPI.FindVotes(VoteQueryModel model)
        {
            throw new NotImplementedException();
        }

        FeeStats INodeAPI.GetFeeStats()
        {
            throw new NotImplementedException();
        }
    }
}