﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Implement disk write queue and async writer thread - used only for write on LOG file
    /// [ThreadSafe]
    /// </summary>
    internal class DiskWriterQueue : IDisposable
    {
        private readonly Stream _stream;

        // async thread controls
        private Task _task;
        private bool _shouldClose = false;

        private readonly ConcurrentQueue<PageBuffer> _queue = new ConcurrentQueue<PageBuffer>();
        private readonly object _queueSync = new object();
        private readonly AsyncManualResetEvent _queueHasItems = new AsyncManualResetEvent();
        private readonly ManualResetEventSlim _queueIsEmpty = new ManualResetEventSlim(true);

        public DiskWriterQueue(Stream stream)
        {
            _stream = stream;
        }

        /// <summary>
        /// Get how many pages are waiting for store
        /// </summary>
        public int Length => _queue.Count;

        /// <summary>
        /// Add page into writer queue and will be saved in disk by another thread. If page.Position = MaxValue, store at end of file (will get final Position)
        /// After this method, this page will be available into reader as a clean page
        /// </summary>
        public void EnqueuePage(PageBuffer page)
        {
            ENSURE(page.Origin == FileOrigin.Log, "async writer must use only for Log file");
            lock (_queueSync)
            {
                _queueIsEmpty.Reset();
                _queue.Enqueue(page);
                _queueHasItems.Set();

                if (_task == null)
                {
                    _task = Task.Factory.StartNew(ExecuteQueue, TaskCreationOptions.LongRunning);
                }
            }
        }

        /// <summary>
        /// Wait until all queue be executed and no more pending pages are waiting for write - be sure you do a full lock database before call this
        /// </summary>
        public void Wait()
        {
            _queueIsEmpty.Wait();
            ENSURE(_queue.Count == 0, "queue should be empty after wait() call");
        }

        /// <summary>
        /// Execute all items in queue sync
        /// </summary>
        private async Task ExecuteQueue()
        {
            while (true)
            {
                if (_queue.TryDequeue(out var page))
                {
                    WritePageToStream(page);
                }
                else
                {
                    lock (_queueSync)
                    {
                        if (_queue.Count > 0) continue;
                        _queueIsEmpty.Set();
                        _queueHasItems.Reset();
                        if (_shouldClose) return;
                    }
                    TryFlushStream();

                    await _queueHasItems.WaitAsync();
                }
            }
        }

        private void TryFlushStream()
        {
            try
            {
                _stream.FlushToDisk();
            }
            catch (IOException)
            {
                // Disk is probably full. This may be unrecoverable problem but until we have enough space in the buffer we may be ok.
            }
        }

        private void WritePageToStream(PageBuffer page)
        {
            if (page == null) return;

            ENSURE(page.ShareCounter > 0, "page must be shared at least 1");

            // set stream position according to page
            _stream.Position = page.Position;

            _stream.Write(page.Array, page.Offset, PAGE_SIZE);

            // release page here (no page use after this)
            page.Release();
        }

        public void Dispose()
        {
            LOG($"disposing disk writer queue (with {_queue.Count} pages in queue)", "DISK");

            _shouldClose = true;
            _queueHasItems.Set(); // unblock the running loop in case there are no items
            
            // run all items in queue before dispose
            this.Wait();
            _task?.Wait();
            _task = null;
        }
    }
}