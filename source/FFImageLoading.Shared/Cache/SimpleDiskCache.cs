﻿using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using FFImageLoading.IO;
using FFImageLoading.Config;
using FFImageLoading.Helpers;

namespace FFImageLoading.Cache
{
	/// <summary>
	/// Disk cache iOS/Android implementation.
	/// </summary>
	public class SimpleDiskCache : IDiskCache
    {
		const int BufferSize = 4096; // Xamarin large object heap threshold is 8K
		string _cachePath;
		ConcurrentDictionary<string, byte> _fileWritePendingTasks = new ConcurrentDictionary<string, byte>();
        readonly SemaphoreSlim _currentWriteLock = new SemaphoreSlim(1, 1);
        Task _currentWrite = Task.FromResult<byte>(1);
		ConcurrentDictionary<string, CacheEntry> _entries = new ConcurrentDictionary<string, CacheEntry> ();

		/// <summary>
		/// Initializes a new instance of the <see cref="FFImageLoading.Cache.SimpleDiskCache"/> class.
		/// </summary>
		/// <param name="cachePath">Cache path.</param>
        public SimpleDiskCache(string cachePath, Configuration configuration)
        {
            _cachePath = Path.GetFullPath(cachePath);
            Configuration = configuration;

            Logger?.Debug("SimpleDiskCache path: " + _cachePath);

            if (!Directory.Exists(_cachePath))
                Directory.CreateDirectory(_cachePath);
            
			InitializeEntries();

            ThreadPool.QueueUserWorkItem(CleanCallback);
        }

        protected Configuration Configuration { get; private set; }
        protected IMiniLogger Logger { get { return Configuration.Logger; } }

		/// <summary>
		/// Creates new cache default instance.
		/// </summary>
		/// <returns>The cache.</returns>
		/// <param name="cacheName">Cache name.</param>
        public static SimpleDiskCache CreateCache(string cacheName, Configuration configuration)
        {
#if __ANDROID__
            var context = new Android.Content.ContextWrapper(Android.App.Application.Context);
            string tmpPath = context.CacheDir.AbsolutePath;
            string cachePath = Path.Combine(tmpPath, cacheName);
#else
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string tmpPath = Path.Combine(documents, "..", "Library", "Caches");
            string cachePath = Path.Combine(tmpPath, cacheName);
#endif

			return new SimpleDiskCache(cachePath, configuration);
        }

		/// <summary>
		/// Adds the file to cache and file saving queue if it does not exists.
		/// </summary>
		/// <param name="key">Key to store/retrieve the file.</param>
		/// <param name="bytes">File data in bytes.</param>
		/// <param name="duration">Specifies how long an item should remain in the cache.</param>
		public virtual async Task AddToSavingQueueIfNotExistsAsync(string key, byte[] bytes, TimeSpan duration, Action writeFinished = null)
		{
            var sanitizedKey = key.ToSanitizedKey();

            if (!_fileWritePendingTasks.TryAdd(sanitizedKey, 1))
            {
                Logger?.Error("Can't save to disk as another write with the same key is pending: " + key);
                return;
            }

            await _currentWriteLock.WaitAsync().ConfigureAwait(false); // Make sure we don't add multiple continuations to the same task
            try
            {
                _currentWrite = _currentWrite.ContinueWith(async t =>
                {
                    await Task.Yield(); // forces it to be scheduled for later

                    try
                    {
                        CacheEntry oldEntry;
                        if (_entries.TryGetValue(sanitizedKey, out oldEntry))
                        {
                            string oldFilepath = Path.Combine(_cachePath, oldEntry.FileName);
                            if (File.Exists(oldFilepath))
                                File.Delete(oldFilepath);
                        }

                        string filename = sanitizedKey + "." + (long)duration.TotalSeconds;
                        string filepath = Path.Combine(_cachePath, filename);

                        await FileStore.WriteBytesAsync(filepath, bytes, CancellationToken.None).ConfigureAwait(false);

                        _entries[sanitizedKey] = new CacheEntry(DateTime.UtcNow, duration, filename);
                        writeFinished?.Invoke();

                        if (Configuration.VerboseLogging)
                            Logger?.Debug(string.Format("File {0} saved to disk cache for key {1}", filepath, key));
                    }
                    catch (Exception ex) // Since we don't observe the task (it's not awaited, we should catch all exceptions)
                    {
                        Logger?.Error(string.Format("An error occured while writing to disk cache for {0}", key), ex);
                    }
                    finally
                    {
                        byte finishedTask;
                        _fileWritePendingTasks.TryRemove(sanitizedKey, out finishedTask);
				    }
			    });
            }
            finally
            {
                _currentWriteLock.Release();
            }
		}

		/// <summary>
		/// Removes the specified cache entry.
		/// </summary>
		/// <param name="key">Key.</param>
		public virtual async Task RemoveAsync(string key)
		{
            var sanitizedKey = key.ToSanitizedKey();

			await WaitForPendingWriteIfExists(sanitizedKey).ConfigureAwait(false);
			CacheEntry entry;
			if (_entries.TryRemove(sanitizedKey, out entry))
			{
				string filepath = Path.Combine(_cachePath, entry.FileName);

				if (File.Exists(filepath))
					File.Delete(filepath);
			}
		}

		/// <summary>
		/// Clears all cache entries.
		/// </summary>
		public virtual async Task ClearAsync()
		{
			while (_fileWritePendingTasks.Count != 0)
			{
				await Task.Delay(20).ConfigureAwait(false);
			}

			Directory.Delete(_cachePath, true);
			Directory.CreateDirectory (_cachePath);
			_entries.Clear();
		}

		/// <summary>
		/// Checks if cache entry exists/
		/// </summary>
		/// <returns>The async.</returns>
		/// <param name="key">Key.</param>
		public virtual Task<bool> ExistsAsync(string key)
		{
            return Task.FromResult(_entries.ContainsKey(key.ToSanitizedKey()));
		}

		/// <summary>
		/// Tries to get cached file as stream.
		/// </summary>
		/// <returns>The get stream.</returns>
		/// <param name="key">Key.</param>
		public virtual async Task<Stream> TryGetStreamAsync(string key)
		{
            var sanitizedKey = key.ToSanitizedKey();
			await WaitForPendingWriteIfExists(sanitizedKey).ConfigureAwait(false);

			try
			{
				CacheEntry entry;
				if (!_entries.TryGetValue(sanitizedKey, out entry))
					return null;

				string filepath = Path.Combine(_cachePath, entry.FileName);
				return FileStore.GetInputStream(filepath, false);
			}
			catch
			{
				return null;
			}	
		}

		public virtual Task<string> GetFilePathAsync(string key)
		{
            var sanitizedKey = key.ToSanitizedKey();

			CacheEntry entry;
			if (!_entries.TryGetValue(sanitizedKey, out entry))
				return Task.FromResult<string>(null);
			
			return Task.FromResult(Path.Combine(_cachePath, entry.FileName));
		}

		protected async Task WaitForPendingWriteIfExists(string key)
		{
			while (_fileWritePendingTasks.ContainsKey(key))
			{
				await Task.Delay(20).ConfigureAwait(false);
			}
		}

		protected void InitializeEntries()
		{
			foreach (FileInfo fileInfo in new DirectoryInfo(_cachePath).EnumerateFiles())
			{
				string key = Path.GetFileNameWithoutExtension(fileInfo.Name);
				TimeSpan duration = GetDuration(fileInfo.Extension);
				_entries.TryAdd(key, new CacheEntry() { Origin = fileInfo.CreationTimeUtc, TimeToLive = duration, FileName = fileInfo.Name });
			}
		}

		protected TimeSpan GetDuration(string text)
		{
			string textToParse = text.Split(new[] { '.'}, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(textToParse))
                return Configuration.DiskCacheDuration;

			int duration;
			return int.TryParse(textToParse, out duration) ? TimeSpan.FromSeconds(duration) : Configuration.DiskCacheDuration;
		}

        protected virtual void CleanCallback(object state)
		{
			KeyValuePair<string, CacheEntry>[] kvps;
			var now = DateTime.UtcNow;
			kvps = _entries.Where(kvp => kvp.Value.Origin + kvp.Value.TimeToLive < now).ToArray();

			foreach (var kvp in kvps)
			{
				CacheEntry oldCacheEntry;
				if (_entries.TryRemove(kvp.Key, out oldCacheEntry)) 
				{
					try 
					{
                        Logger.Debug(string.Format("SimpleDiskCache: Removing expired file {0}", kvp.Key));
						File.Delete(Path.Combine(_cachePath, kvp.Key));
					} 
					// Analysis disable once EmptyGeneralCatchClause
					catch 
					{
					}
				}
			}
		}
    }
}
