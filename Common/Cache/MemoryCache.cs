﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Cache
{
    public class MemoryCache : IMemoryCache
    {        
        readonly TimeSpan _expirationScanFrequency;

        readonly ConcurrentDictionary<object, CacheEntry> _cacheEntries;

        readonly ConcurrentDictionary<object, TagEntry> _tagEntries;

        public MemoryCache(TimeSpan expirationScanFrequency)
        {
            if (expirationScanFrequency <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(expirationScanFrequency));
            }

            _expirationScanFrequency = expirationScanFrequency;

            _cacheEntries = new ConcurrentDictionary<object, CacheEntry>();

            _tagEntries = new ConcurrentDictionary<object, TagEntry>();
        }

        public MemoryCache()
            : this(TimeSpan.FromMinutes(1))
        {
        }

        public bool TryGet<T>(object key, out T value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            ScheduleScanForExpiredEntries();

            CacheEntry cacheEntry;
            if (!_cacheEntries.TryGetValue(key, out cacheEntry))
            {
                value = default(T);
                return false;
            }

            if (cacheEntry.CheckIfExpired())
            {
                RemoveCacheEntry(key, cacheEntry);

                value = default(T);
                return false;
            }

            value = cacheEntry.GetValue<T>();
            return true;
        }
        
        public void Add<T>(object key, object[] tags, bool isSliding, TimeSpan lifetime, T value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (lifetime <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(lifetime));
            }

            ScheduleScanForExpiredEntries();

            CacheEntry createdEntry, deletedEntry = null;

            createdEntry = new CacheEntry(isSliding, lifetime, value);
            
            _cacheEntries.AddOrUpdate(key, createdEntry, (_, updatedEntry) =>
            {
                deletedEntry = updatedEntry;
                return createdEntry;
            });

            if (deletedEntry != null)
            {
                deletedEntry.MarkAsExpired();

                UnbindFromTagEntries(deletedEntry);
            }

            if (tags != null)
            {
                BindToTagEntries(key, tags, createdEntry);
            }
        }

        public T GetOrAdd<T>(
            object key, object[] tags, bool isSliding, TimeSpan lifetime, Func<T> valueFactory)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (lifetime <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(lifetime));
            }
            if (valueFactory == null) throw new ArgumentNullException(nameof(valueFactory));

            ScheduleScanForExpiredEntries();

            CacheEntry actualEntry, createdEntry;

            if (!_cacheEntries.TryGetValue(key, out actualEntry) || actualEntry.CheckIfExpired())
            {
                createdEntry = new CacheEntry(isSliding, lifetime, new LazyValue<T>(valueFactory));

                actualEntry = GetOrAddCacheEntry(key, tags, createdEntry);
            }

            return actualEntry.GetValue<T>();
        }

        public Task<T> GetOrAddAsync<T>(
            object key, object[] tags, bool isSliding, TimeSpan lifetime, Func<Task<T>> taskFactory)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (lifetime <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(lifetime));
            }
            if (taskFactory == null) throw new ArgumentNullException(nameof(taskFactory));

            ScheduleScanForExpiredEntries();

            CacheEntry actualEntry, createdEntry;

            if (!_cacheEntries.TryGetValue(key, out actualEntry) || actualEntry.CheckIfExpired())
            {
                createdEntry = new CacheEntry(isSliding, lifetime, new LazyTask<T>(taskFactory));

                actualEntry = GetOrAddCacheEntry(key, tags, createdEntry);
            }

            return actualEntry.GetTask<T>();
        }

        private CacheEntry GetOrAddCacheEntry(object key, object[] tags, CacheEntry createdEntry)
        {
            CacheEntry actualEntry, deletedEntry = null;

            actualEntry = _cacheEntries.AddOrUpdate(key, createdEntry, (_, updatedEntry) =>
            {
                if (updatedEntry.CheckIfExpired())
                {
                    deletedEntry = updatedEntry;
                    return createdEntry;
                }
                else
                {
                    deletedEntry = null;
                    return updatedEntry;
                }
            });

            if (deletedEntry != null)
            {
                deletedEntry.MarkAsExpired();

                UnbindFromTagEntries(deletedEntry);
            }

            if (tags != null && actualEntry == createdEntry)
            {
                BindToTagEntries(key, tags, createdEntry);
            }
            
            return actualEntry;
        }

        private void BindToTagEntries(object key, object[] tags, CacheEntry cacheEntry)
        {
            cacheEntry.TagEntries = new HashSet<TagEntry>();

            foreach (object tag in tags)
            {
                TagEntry tagEntry;

                do
                {
                    tagEntry = _tagEntries.GetOrAdd(tag, _ => new TagEntry(cacheEntry, key));

                    tagEntry.CacheEntries.TryAdd(cacheEntry, key);

                    if (cacheEntry.IsExpired || tagEntry.IsRemoved)
                    {
                        RemoveCacheEntry(key, cacheEntry);
                        return;
                    }
                }
                while (tagEntry.IsEvicted);
                
                lock (cacheEntry.TagEntries)
                {
                    cacheEntry.TagEntries.Add(tagEntry);
                }
            }
        }
        
        private static void UnbindFromTagEntries(CacheEntry cacheEntry)
        {
            if (cacheEntry.TagEntries != null)
            {
                lock (cacheEntry.TagEntries)
                {
                    foreach (TagEntry tagEntry in cacheEntry.TagEntries)
                    {
                        if (tagEntry.IsActive)
                        {
                            object _;
                            tagEntry.CacheEntries.TryRemove(cacheEntry, out _);
                        }
                    }
                }
            }
        }

        private void RemoveCacheEntry(object key, CacheEntry cacheEntry)
        {
            _cacheEntries.Remove(key, cacheEntry);

            cacheEntry.MarkAsExpired();

            UnbindFromTagEntries(cacheEntry);
        }

        public void Remove(object key)
        {
            CacheEntry cacheEntry;
            if (_cacheEntries.TryRemove(key, out cacheEntry))
            {
                cacheEntry.MarkAsExpired();

                UnbindFromTagEntries(cacheEntry);
            }
        }
        
        public void RemoveByTag(object tag)
        {
            TagEntry tagEntry;
            if (_tagEntries.TryRemove(tag, out tagEntry))
            {
                tagEntry.MarkAsRemoved();

                foreach (var cachePair in tagEntry.CacheEntries)
                {
                    object key = cachePair.Value;
                    CacheEntry cacheEntry = cachePair.Key;

                    RemoveCacheEntry(key, cacheEntry);
                }
            }
        }

        private long _lastExpirationScanTicks = 0;

        private int _cleanupIsRunning = 0;

        private void ScheduleScanForExpiredEntries()
        {
            long nextExpirationScanTicks = (DateTime.UtcNow - _expirationScanFrequency).Ticks;

            if (nextExpirationScanTicks > Volatile.Read(ref _lastExpirationScanTicks))
            {
                if (Interlocked.CompareExchange(ref _cleanupIsRunning, 1, 0) == 0)
                {
                    Volatile.Write(ref _lastExpirationScanTicks, DateTime.UtcNow.Ticks);

                    ThreadPool.QueueUserWorkItem(state => ScanForExpiredEntries((MemoryCache)state), this);
                }
            }
        }

        private static void ScanForExpiredEntries(MemoryCache cache)
        {
            DateTime utcNow = DateTime.UtcNow;

            foreach (var cachePair in cache._cacheEntries)
            {
                object key = cachePair.Key;
                CacheEntry cacheEntry = cachePair.Value;

                if (cacheEntry.CheckIfExpired(utcNow))
                {
                    cache.RemoveCacheEntry(key, cacheEntry);
                }
            }

            foreach (var tagPair in cache._tagEntries)
            {
                object tag = tagPair.Key;
                TagEntry tagEntry = tagPair.Value;

                if (tagEntry.CacheEntries.IsEmpty)
                {
                    cache.ScatterEvictedTagEntry(tag, tagEntry);
                }
            }

            Volatile.Write(ref cache._cleanupIsRunning, 0);
        }

        private void ScatterEvictedTagEntry(object tag, TagEntry tagEntry)
        {
            if (_tagEntries.Remove(tag, tagEntry))
            {
                tagEntry.MarkAsEvicted();

                foreach (var cachePair in tagEntry.CacheEntries)
                {
                    object key = cachePair.Value;
                    CacheEntry cacheEntry = cachePair.Key;

                    do
                    {
                        tagEntry = _tagEntries.GetOrAdd(tag, _ => new TagEntry(cacheEntry, key));

                        tagEntry.CacheEntries.TryAdd(cacheEntry, key);

                        if (cacheEntry.IsExpired || tagEntry.IsRemoved)
                        {
                            RemoveCacheEntry(key, cacheEntry);
                            continue;
                        }
                    }
                    while (tagEntry.IsEvicted);
                }
            }
        }
    }
}
