﻿using NBitcoin;
using NBitcoin.Protocol;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Stratis.Bitcoin.FullNode.BlockPulling
{
	public interface ILookaheadBlockPuller
	{
		Block TryGetLookahead(int count);
	}
	public abstract class LookaheadBlockPuller : BlockPuller, ILookaheadBlockPuller
	{
		class DownloadedBlock
		{
			public int Length;
			public Block Block;
		}
		const int BLOCK_SIZE = 2000000;
		public LookaheadBlockPuller()
		{
			MaxBufferedSize = BLOCK_SIZE * 10;
			MinimumLookahead = 4;
			MaximumLookahead = 2000;
		}


		public int MinimumLookahead
		{
			get;
			set;
		}

		public int MaximumLookahead
		{
			get;
			set;
		}

		int _ActualLookahead;
		public int ActualLookahead
		{
			get
			{
				return 500;
				//return Math.Min(MaximumLookahead, Math.Max(MinimumLookahead, _ActualLookahead));
			}
			private set
			{
				_ActualLookahead = Math.Min(MaximumLookahead, Math.Max(MinimumLookahead, value));
			}
		}

		public int DownloadedCount
		{
			get
			{
				return _DownloadedBlocks.Count;
			}
		}

		public ChainedBlock Location
		{
			get
			{
				return _Location;
			}
		}

		int _CurrentDownloading = 0;

		public int MaxBufferedSize
		{
			get;
			set;
		}


		public bool Stalling
		{
			get;
			internal set;
		}

		long _CurrentSize;
		ConcurrentDictionary<uint256, DownloadedBlock> _DownloadedBlocks = new ConcurrentDictionary<uint256, DownloadedBlock>();

		ConcurrentChain _Chain;
		ChainedBlock _Location;
		ChainedBlock _LookaheadLocation;
		public ChainedBlock LookaheadLocation
		{
			get
			{
				return _LookaheadLocation;
			}
		}

		public override void SetLocation(ChainedBlock tip)
		{
			if(tip == null)
				throw new ArgumentNullException("tip");
			_Location = tip;
		}

		public ConcurrentChain Chain
		{
			get
			{
				return _Chain;
			}
		}

		int lastLookaheadUpdate = 0;
		public override Block NextBlock()
		{
			if(_Chain == null)
				ReloadChain();


			if(lastLookaheadUpdate > ActualLookahead)
			{
				CalculateLookahead();
				lastLookaheadUpdate = 0;
			}
			lastLookaheadUpdate++;
			rollingDownloadedCount = ApproxRollingAverage(rollingDownloadedCount, _DownloadedBlocks.Count);

			if(_LookaheadLocation == null)
			{
				AskBlocks();
				AskBlocks();
			}
			var block = NextBlockCore();
			if((_LookaheadLocation.Height - _Location.Height) <= ActualLookahead)
				AskBlocks();

			return block;
		}

		private decimal ApproxRollingAverage(decimal avg, int sample)
		{
			avg -= avg / ActualLookahead;
			avg += (decimal)sample / ActualLookahead;
			return avg;
		}

		decimal rollingDownloadedCount = 0;
		public int RollingAverageDownloadedCount
		{
			get
			{
				return (int)rollingDownloadedCount;
			}
		}

		// If blocks ActualLookahead is 8: 
		// If the number of downloaded block reach 2 or below, then ActualLookahead will be multiplied by 1.1. 
		// If it reach 14 or above, it will be divided by 1.1.
		private void CalculateLookahead()
		{
			decimal tolerance = 0.25m;
			var margin = Math.Max(tolerance * ActualLookahead, 1);
			if(rollingDownloadedCount <= margin)
				ActualLookahead = (int)Math.Max(ActualLookahead * 1.1m, ActualLookahead + 1);
			else if(rollingDownloadedCount >= Math.Min(MaximumLookahead, ActualLookahead * 1.5m) - margin)
				ActualLookahead = (int)Math.Min(ActualLookahead / 1.1m, ActualLookahead - 1);
		}

		public Block TryGetLookahead(int count)
		{
			var chainedBlock = _Chain.GetBlock(_Location.Height + 1 + count);
			if(chainedBlock == null)
				return null;
			var block = _DownloadedBlocks.TryGet(chainedBlock.HashBlock);
			if(block == null)
				return null;
			return block.Block;
		}

		public override void Reject(Block block, RejectionMode rejectionMode)
		{
			var h = block.GetHash();
			if(_Chain.Contains(h))
				ReloadChain();
			_RejectedHashes.TryAdd(h, h);
		}

		ConcurrentDictionary<uint256, uint256> _RejectedHashes = new ConcurrentDictionary<uint256, uint256>();
		public bool IsRejected(uint256 blockHash)
		{
			return _RejectedHashes.ContainsKey(blockHash);
		}

		protected abstract void AskBlocks(ChainedBlock[] downloadRequests);
		protected abstract ConcurrentChain ReloadChainCore();
		private void ReloadChain()
		{
			lock(_ChainLock)
			{
				_Chain = ReloadChainCore();
			}
		}

		AutoResetEvent _Consumed = new AutoResetEvent(false);
		AutoResetEvent _Pushed = new AutoResetEvent(false);

		/// <summary>
		/// If true, the puller is a bottleneck
		/// </summary>
		public bool IsStalling
		{
			get;
			internal set;
		}

		/// <summary>
		/// If true, the puller consumer is a bottleneck
		/// </summary>
		public bool IsFull
		{
			get;
			internal set;
		}

		protected void PushBlock(int length, Block block)
		{
			var hash = block.Header.GetHash();
			var header = _Chain.GetBlock(hash);
			while(_CurrentSize + length >= MaxBufferedSize && header.Height != _Location.Height + 1)
			{
				IsFull = true;
				_Consumed.WaitOne(1000);
			}
			IsFull = false;
			_DownloadedBlocks.TryAdd(hash, new DownloadedBlock() { Block = block, Length = length });
			_CurrentSize += length;
			_Pushed.Set();
		}

		object _ChainLock = new object();

		private void AskBlocks()
		{
			if(_Location == null)
				throw new InvalidOperationException("SetLocation should have been called");
			if(_LookaheadLocation == null && !_Chain.Contains(_Location))
				return;
			if(_LookaheadLocation != null && !_Chain.Contains(_LookaheadLocation))
				_LookaheadLocation = null;

			ChainedBlock[] downloadRequests = null;
			lock(_ChainLock)
			{
				ChainedBlock lookaheadBlock = _LookaheadLocation ?? _Location;
				ChainedBlock nextLookaheadBlock = _Chain.GetBlock(Math.Min(lookaheadBlock.Height + ActualLookahead, _Chain.Height));
				_LookaheadLocation = nextLookaheadBlock;

				downloadRequests = new ChainedBlock[nextLookaheadBlock.Height - lookaheadBlock.Height];
				if(downloadRequests.Length == 0)
					return;
				for(int i = 0; i < downloadRequests.Length; i++)
				{
					downloadRequests[i] = _Chain.GetBlock(lookaheadBlock.Height + 1 + i);
				}
			}
			AskBlocks(downloadRequests);
		}

		static int[] waitTime = new[] { 1, 10, 100, 1000 };
		private Block NextBlockCore()
		{
			int i = 0;
			while(true)
			{
				var header = _Chain.GetBlock(_Location.Height + 1);
				DownloadedBlock block;
				if(header != null && _DownloadedBlocks.TryRemove(header.HashBlock, out block))
				{
					IsStalling = false;
					_Location = header;
					Interlocked.Add(ref _CurrentSize, -block.Length);
					_Consumed.Set();
					return block.Block;
				}
				else
				{
					IsStalling = true;
					_Pushed.WaitOne(waitTime[i]);
				}
				i = Math.Min(i + 1, waitTime.Length - 1);
			}
		}
	}
}
