﻿using Lyra.Core.API;
using Lyra.Core.Blocks;
using Lyra.Data.API;
using Lyra.Data.Crypto;
using Neo;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Lyra.Core.Decentralize
{
    // this class never fail.
    // if failed, then all seeds are failed. if all seeds failed, then why should this exists.
    public class LyraClientForNode
    {
        DagSystem _sys;
        private LyraAggregatedClient _client;
        private AccountHeightAPIResult _syncInfo;
        private List<KeyValuePair<string, string>> _validNodes;

        public LyraAggregatedClient Client { get => _client; set => _client = value; }

        public LyraClientForNode(DagSystem sys)
        {
            _sys = sys;
        }

        public LyraClientForNode(DagSystem sys, List<KeyValuePair<string, string>> validNodes)
        {
            _sys = sys;
            _validNodes = validNodes;
        }

        public async Task<string> SignAPICallAsync()
        {
            try
            {
                if(_client == null)
                {
                    _client = await FindValidSeedForSyncAsync(_sys);                    
                }
                    
                return Signatures.GetSignature(_sys.PosWallet.PrivateKey, _syncInfo.SyncHash, _sys.PosWallet.AccountId);
            }
            catch(Exception ex)
            {
                if (ex is TaskCanceledException || ex is HttpRequestException || ex.Message == "Web Api Failed.")
                {
                    // retry
                    _client = await FindValidSeedForSyncAsync(_sys);
                    return await SignAPICallAsync();
                }
                else
                    throw ex;
            }
        }

        internal async Task<BlockAPIResult> GetLastConsolidationBlockAsync()
        {
            try
            {
                if (_client == null)
                    _client = await FindValidSeedForSyncAsync(_sys);

                return await _client.GetLastConsolidationBlock();
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException || ex is HttpRequestException || ex.Message == "Web Api Failed.")
                {
                    // retry
                    _client = await FindValidSeedForSyncAsync(_sys);
                    return await GetLastConsolidationBlockAsync();
                }
                else
                    throw ex;
            }
            
        }

        internal async Task<MultiBlockAPIResult> GetBlocksByConsolidation(string consolidationHash)
        {
            try
            {
                if (_client == null)
                    _client = await FindValidSeedForSyncAsync(_sys);

                var result = await _client.GetBlocksByConsolidation(_sys.PosWallet.AccountId, await SignAPICallAsync(), consolidationHash);
                if (result.ResultCode == APIResultCodes.APISignatureValidationFailed)
                {
                    _syncInfo = await _client.GetSyncHeight();

                    return await GetBlocksByConsolidation(consolidationHash);
                }
                else
                    return result;
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException || ex is HttpRequestException || ex.Message == "Web Api Failed.")
                {
                    // retry
                    _client = await FindValidSeedForSyncAsync(_sys);
                    return await GetBlocksByConsolidation(consolidationHash);
                }
                else
                    throw ex;
            }
            
        }

        internal async Task<MultiBlockAPIResult> GetConsolidationBlocks(long startConsHeight)
        {
            try
            {
                if (_client == null)
                    _client = await FindValidSeedForSyncAsync(_sys);

                var result = await _client.GetConsolidationBlocks(_sys.PosWallet.AccountId, await SignAPICallAsync(), startConsHeight, 10);
                if (result.ResultCode == APIResultCodes.APISignatureValidationFailed)
                {
                    _syncInfo = await _client.GetSyncHeight();

                    return await GetConsolidationBlocks(startConsHeight);
                }
                else
                    return result;
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException || ex is HttpRequestException || ex.Message == "Web Api Failed.")
                {
                    // retry
                    _client = await FindValidSeedForSyncAsync(_sys);
                    return await GetConsolidationBlocks(startConsHeight);
                }
                else
                    throw ex;
            }
        }

        public async Task<BlockAPIResult> GetBlockByHash(string Hash)
        {
            try
            {
                if (_client == null)
                    _client = await FindValidSeedForSyncAsync(_sys);

                return await _client.GetBlockByHash(_sys.PosWallet.AccountId, Hash, "");
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException || ex is HttpRequestException || ex.Message == "Web Api Failed.")
                {
                    // retry
                    _client = await FindValidSeedForSyncAsync(_sys);
                    return await GetBlockByHash(Hash);
                }
                else
                    throw ex;
            }            
        }

        public async Task<GetSyncStateAPIResult> GetSyncState()
        {
            try
            {
                if (_client == null)
                    _client = await FindValidSeedForSyncAsync(_sys);

                return await _client.GetSyncState();
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException || ex is HttpRequestException || ex.Message == "Web Api Failed.")
                {
                    // retry
                    _client = await FindValidSeedForSyncAsync(_sys);
                    return await GetSyncState();
                }
                else
                    throw ex;
            }
        }

        public async Task<MultiBlockAPIResult> GetBlocksByTimeRange(DateTime startTime, DateTime endTime)
        {
            try
            {
                if (_client == null)
                    _client = await FindValidSeedForSyncAsync(_sys);

                return await _client.GetBlocksByTimeRange(startTime, endTime);
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException || ex is HttpRequestException || ex.Message == "Web Api Failed.")
                {
                    // retry
                    _client = await FindValidSeedForSyncAsync(_sys);
                    return await GetBlocksByTimeRange(startTime, endTime);
                }
                else
                    throw ex;
            }
        }

        public async Task<GetListStringAPIResult> GetBlockHashesByTimeRange(DateTime startTime, DateTime endTime)
        {
            try
            {
                if (_client == null)
                    _client = await FindValidSeedForSyncAsync(_sys);

                return await _client.GetBlockHashesByTimeRange(startTime, endTime);
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException || ex is HttpRequestException || ex.Message == "Web Api Failed.")
                {
                    // retry
                    _client = await FindValidSeedForSyncAsync(_sys);
                    return await GetBlockHashesByTimeRange(startTime, endTime);
                }
                else
                    throw ex;
            }
        }

        public async Task<BlockAPIResult> GetServiceGenesisBlock()
        {
            try
            {
                if (_client == null)
                    _client = await FindValidSeedForSyncAsync(_sys);

                return await _client.GetServiceGenesisBlock();
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException || ex is HttpRequestException || ex.Message == "Web Api Failed.")
                {
                    // retry
                    _client = await FindValidSeedForSyncAsync(_sys);
                    return await GetServiceGenesisBlock();
                }
                else
                    throw ex;
            }
        }

        public async Task<BlockAPIResult> GetLastServiceBlock()
        {
            try
            {
                if (_client == null)
                    _client = await FindValidSeedForSyncAsync(_sys);

                return await _client.GetLastServiceBlock();
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException || ex is HttpRequestException || ex.Message == "Web Api Failed.")
                {
                    // retry
                    _client = await FindValidSeedForSyncAsync(_sys);
                    return await GetLastServiceBlock();
                }
                else
                    throw ex;
            }
        }

        public async Task<LyraAggregatedClient> FindValidSeedForSyncAsync(DagSystem sys)
        {
            var client = new LyraAggregatedClient(Neo.Settings.Default.LyraNode.Lyra.NetworkId);

            var rand = new Random();
            int ndx;

            using (RNGCryptoServiceProvider rg = new RNGCryptoServiceProvider())
            {
                do
                {
                    byte[] rno = new byte[5];
                    rg.GetBytes(rno);
                    int randomvalue = BitConverter.ToInt32(rno, 0);

                    ndx = randomvalue % ProtocolSettings.Default.SeedList.Length;
                } while (ndx < 0 || sys.PosWallet.AccountId == ProtocolSettings.Default.StandbyValidators[ndx]);
            }

            var addr = ProtocolSettings.Default.SeedList[ndx].Split(':')[0];

            try
            {
                await client.InitAsync(addr);
            }
            catch(Exception ex)
            {

            }
            _syncInfo = await client.GetSyncHeight();

            return client;
/*            ushort peerPort = 4504;
            if (Neo.Settings.Default.LyraNode.Lyra.NetworkId == "mainnet")
                peerPort = 5504;
            
            if (_validNodes == null || _validNodes.Count == 0)
            {
                do
                {
                    var rand = new Random();
                    int ndx;

                    using (RNGCryptoServiceProvider rg = new RNGCryptoServiceProvider())
                    {
                        do
                        {
                            byte[] rno = new byte[5];
                            rg.GetBytes(rno);
                            int randomvalue = BitConverter.ToInt32(rno, 0);

                            ndx = randomvalue % ProtocolSettings.Default.SeedList.Length;
                        } while (ndx < 0 || sys.PosWallet.AccountId == ProtocolSettings.Default.StandbyValidators[ndx]);
                    }

                    var addr = ProtocolSettings.Default.SeedList[ndx].Split(':')[0];
                    var apiUrl = $"https://{addr}:{peerPort}/api/Node/";
                    //_log.LogInformation("Platform {1} Use seed node of {0}", apiUrl, Environment.OSVersion.Platform);
                    var client = LyraRestClient.Create(Neo.Settings.Default.LyraNode.Lyra.NetworkId, Environment.OSVersion.Platform.ToString(), "LyraNoded", "1.7", apiUrl);
                    var mode = await client.GetSyncState();
                    if (mode.ResultCode == APIResultCodes.Success)
                    {
                        _syncInfo = await client.GetSyncHeight();
                        return client;
                    }
                    await Task.Delay(10000);    // incase of hammer
                } while (true);
            }
            else
            {
                var rand = new Random();
                while(true)
                {
                    var addr = _validNodes[rand.Next(0, _validNodes.Count - 1)].Value;
                    var apiUrl = $"https://{addr}:{peerPort}/api/Node/";
                    var client = LyraRestClient.Create(Neo.Settings.Default.LyraNode.Lyra.NetworkId, Environment.OSVersion.Platform.ToString(), "LyraNoded", "1.7", apiUrl);
                    var mode = await client.GetSyncState();
                    if (mode.ResultCode == APIResultCodes.Success)
                    {
                        _syncInfo = await client.GetSyncHeight();
                        return client;
                    }
                    await Task.Delay(10000);    // incase of hammer
                }
            }*/
        }
    }
}
