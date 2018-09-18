﻿using DLT.Meta;
using DLTNode;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLT
{
    class WalletState
    {
        private readonly object stateLock = new object();
        private readonly Dictionary<string, Wallet> walletState = new Dictionary<string, Wallet>(); // The entire wallet list
        private string cachedChecksum = "";
        private Dictionary<string, Wallet> wsDelta = null;
        private string cachedDeltaChecksum = "";

        /* Size:
         * 10_000 wallets: ~510 KB
         * 100_000 wallets: ~5 MB
         * 10_000_000 wallets: ~510 MB (312 MB)
         * 
         * Keys only:
         * 10_000_000 addresses: 350 MB (176 MB)
         * 
         */

        public int numWallets { get => walletState.Count; }
        public bool hasSnapshot { get => wsDelta != null; }

        public WalletState()
        {
        }

        public WalletState(IEnumerable<Wallet> genesisState)
        {
            Logging.info(String.Format("Generating genesis WalletState with {0} wallets.", genesisState.Count()));
            foreach(Wallet w in genesisState)
            {
                Logging.info(String.Format("-> Genesis wallet ( {0} ) : {1}.", w.id, w.balance));
                walletState.Add(w.id, w);
            }
        }

        // Construct the walletstate from and older one
        public WalletState(WalletState oldWS)
        {
            walletState = new Dictionary<string, Wallet>(oldWS.walletState);
            cachedChecksum = oldWS.cachedChecksum;
            wsDelta = new Dictionary<string, Wallet>(oldWS.wsDelta);
            cachedDeltaChecksum = oldWS.cachedDeltaChecksum;
        }

        public void clear()
        {
            Logging.info("Clearing wallet state!!");
            lock(stateLock)
            {
                walletState.Clear();
                cachedChecksum = "";
                wsDelta = null;
                cachedDeltaChecksum = "";

            }
        }

        public bool snapshot()
        {
            lock (stateLock)
            {
                if (wsDelta != null)
                {
                    Logging.warn("Unable to create WalletState snapshot, because a snapshot already exists.");
                    return false;
                }
                Logging.info("Creating a WalletState snapshot.");
                wsDelta = new Dictionary<string, Wallet>();
                return true;
            }
        }

        public void revert()
        {
            lock (stateLock)
            {
                if (wsDelta != null)
                {
                    Logging.info(String.Format("Reverting WalletState snapshot ({0} wallets).", wsDelta.Count));
                    wsDelta = null;
                    cachedDeltaChecksum = "";
                }
            }
        }

        public void commit()
        {
            lock (stateLock)
            {
                if (wsDelta != null)
                {
                    Logging.info(String.Format("Committing WalletState snapshot. Wallets in snapshot: {0}.", wsDelta.Count));
                    foreach (var wallet in wsDelta)
                    {
                        walletState.AddOrReplace(wallet.Key, wallet.Value);
                    }
                    wsDelta = null;
                    cachedDeltaChecksum = "";
                    cachedChecksum = "";
                }
            }
        }

        public IxiNumber getWalletBalance(string id, bool snapshot = false)
        {
            return getWallet(id, snapshot).balance;
        }



        public Wallet getWallet(string id, bool snapshot = false)
        {
            lock (stateLock)
            {
                Wallet candidateWallet = new Wallet(id, (ulong)0);
                if (walletState.ContainsKey(id))
                {
                    candidateWallet.data = walletState[id].data;
                    candidateWallet.balance = walletState[id].balance;
                    candidateWallet.nonce = walletState[id].nonce;
                }
                if (snapshot)
                {
                    if (wsDelta != null && wsDelta.ContainsKey(id))
                    {
                        candidateWallet.data = wsDelta[id].data;
                        candidateWallet.balance = wsDelta[id].balance;
                        candidateWallet.nonce = wsDelta[id].nonce;
                    }
                }
                return candidateWallet;
            }
        }


        // Sets the wallet balance for a specified wallet
        public void setWalletBalance(string id, IxiNumber balance, bool snapshot = false, ulong nonce = 0)
        {
            lock (stateLock)
            {
                Wallet wallet = new Wallet(id, balance);

                // Apply nonce if needed
                if (nonce > 0)
                    wallet.nonce = nonce;

                if (snapshot == false)
                {
                    walletState.AddOrReplace(id, wallet);
                    cachedChecksum = "";
                }
                else
                {
                    if (wsDelta == null)
                    {
                        Logging.warn(String.Format("Attempted to apply wallet state to the snapshot, but it does not exist."));
                        return;
                    }
                    wsDelta.AddOrReplace(id, wallet);
                    cachedDeltaChecksum = "";
                }
            }
        }

        public void setWalletNonce(string id, ulong nonce, bool snapshot = false)
        {
            lock (stateLock)
            {
                Wallet wallet = getWallet(id, snapshot);

                if(wallet == null)
                {
                    Logging.warn(String.Format("Attempted to set nonce {0} for wallet {1} that does not exist.", nonce, id));
                    return;
                }

                setWalletBalance(id, wallet.balance, snapshot, nonce);
            }
        }

        public string calculateWalletStateChecksum(bool snapshot = false)
        {
            lock (stateLock)
            {
                if (snapshot == false && cachedChecksum != "")
                {
                    return cachedChecksum;
                }
                else if (snapshot == true && cachedDeltaChecksum != "")
                {
                    return cachedDeltaChecksum;
                }
                // TODO: This could get unwieldy above ~100M wallet addresses. We have to implement sharding by then.
                SortedSet<string> eligible_addresses = new SortedSet<string>(walletState.Keys);
                if (snapshot == true)
                {
                    foreach (string addr in wsDelta.Keys)
                    {
                        eligible_addresses.Add(addr);
                    }
                }
                // TODO: This is probably not the optimal way to do this. Maybe we could do it by blocks to reduce calls to sha256
                // Note: addresses are fixed size
                string checksum = Crypto.sha256("IXIAN-DLT");
                foreach (string addr in eligible_addresses)
                {
                    string wallet_checksum = getWallet(addr, snapshot).calculateChecksum();
                    checksum = Crypto.sha256(checksum + wallet_checksum);
                }
                if (snapshot == false)
                {
                    cachedChecksum = checksum;
                }
                else
                {
                    cachedDeltaChecksum = checksum;
                }
                return checksum;
            }
        }

        public WsChunk[] getWalletStateChunks(int chunk_size)
        {
            lock(stateLock)
            {
                ulong block_num = Node.blockChain.getLastBlockNum();
                int num_chunks = walletState.Count / chunk_size + 1;
                Logging.info(String.Format("Preparing {0} chunks of walletState. Total wallets: {1}", num_chunks, walletState.Count));
                WsChunk[] chunks = new WsChunk[num_chunks];
                for(int i=0;i<num_chunks;i++)
                {
                    chunks[i] = new WsChunk
                    {
                        blockNum = block_num,
                        chunkNum = i,
                        wallets = walletState.Skip(i * chunk_size).Take(chunk_size).Select(x => x.Value).ToArray()
                    };
                }
                Logging.info(String.Format("Prepared {0} WalletState chunks with {1} total wallets.",
                    num_chunks,
                    chunks.Sum(x => x.wallets.Count())));
                return chunks;
            }
        }

        public void setWalletChunk(Wallet[] wallets)
        {
            lock (stateLock)
            {
                if (wsDelta != null)
                {
                    // TODO: need to return an error to the caller, otherwise sync process might simply hang
                    Logging.error("Attempted to apply a WalletState chunk, but snapshots exist!");
                    return;
                }
                foreach (Wallet w in wallets)
                {
                    walletState.AddOrReplace(w.id, w);
                }
                cachedChecksum = "";
                cachedDeltaChecksum = "";
            }
        }

        // Calculates the entire IXI supply based on the latest wallet state
        public IxiNumber calculateTotalSupply()
        {
            IxiNumber total = new IxiNumber();
            try
            {
                foreach (var item in walletState)
                {
                    Wallet wal = (Wallet)item.Value;
                    total = total + wal.balance;
                }
            }
            catch(Exception e)
            {
                Logging.error(string.Format("Exception calculating total supply: {0}", e.Message));
            }

            return total;
        }

        // only returns 10 wallets from base state (no snapshotting)
        public Wallet[] debugGetWallets()
        {
            lock (stateLock)
            {
                return walletState.Take(50).Select(x => x.Value).ToArray();
            }
        }
    }
}
