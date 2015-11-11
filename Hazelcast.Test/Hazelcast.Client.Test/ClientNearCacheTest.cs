// Copyright (c) 2008-2015, Hazelcast, Inc. All Rights Reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using Hazelcast.Client.Proxy;
using Hazelcast.Config;
using Hazelcast.Core;
using Hazelcast.IO.Serialization;
using Hazelcast.Util;
using NUnit.Framework;

namespace Hazelcast.Client.Test
{
    [TestFixture]
    public class ClientNearCacheTest : HazelcastBaseTest
    {
        [SetUp]
        public void Init()
        {
            _map = Client.GetMap<object, object>("nearCachedMap-" + TestSupport.RandomString());
        }

        [TearDown]
        public void Destroy()
        {
            _map.Clear();
            _map.Destroy();
        }

        private const int MaxSize = 1000;

        protected override void ConfigureClient(ClientConfig config)
        {
            base.ConfigureClient(config);

            var defaultConfig = new NearCacheConfig()
                .SetInvalidateOnChange(false)
                .SetInMemoryFormat(InMemoryFormat.Object)
                .SetEvictionPolicy("None")
                .SetMaxSize(MaxSize);
            config.AddNearCacheConfig("nearCachedMap*", defaultConfig);

            var invalidateConfig = new NearCacheConfig()
                .SetInvalidateOnChange(true);
            config.AddNearCacheConfig("nearCacheMapInvalidate*", invalidateConfig);

            var lruConfig = new NearCacheConfig()
                .SetEvictionPolicy("Lru")
                .SetInvalidateOnChange(false)
                .SetMaxSize(MaxSize);
            config.AddNearCacheConfig("nearCacheMapLru*", lruConfig);

            var lfuConfig = new NearCacheConfig()
                .SetEvictionPolicy("Lfu")
                .SetInvalidateOnChange(false)
                .SetMaxSize(MaxSize);
            config.AddNearCacheConfig("nearCacheMapLfu*", lfuConfig);

            var ttlConfig = new NearCacheConfig()
                .SetInvalidateOnChange(false)
                .SetTimeToLiveSeconds(1);
            config.AddNearCacheConfig("nearCacheTtl*", ttlConfig);

            var idleConfig = new NearCacheConfig()
                .SetInvalidateOnChange(false)
                .SetMaxIdleSeconds(1);
            config.AddNearCacheConfig("nearCacheIdle*", idleConfig);
        }

        private IMap<object, object> _map;

        private ClientNearCache GetNearCache<TK, TV>(IMap<TK, TV> map)
        {
            return (map as ClientMapProxy<TK, TV>).NearCache;
        }

        private IData ToKeyData(object k)
        {
            return ((HazelcastClientProxy) Client).GetClient().GetSerializationService().ToData(k);
        }

        private void TestInvalidate(Action<IMap<string, string>, string> invalidatingAction)
        {
            var map = Client.GetMap<string, string>("nearCacheMapInvalidate-" + TestSupport.RandomString());
            try
            {
                map.Put("key", "value");

                var val = map.Get("key");
                Assert.AreEqual("value", val);

                var clientNearCache = GetNearCache(map);

                Assert.AreEqual(1, clientNearCache.Cache.Count);

                var client = CreateClient();
                try
                {
                    invalidatingAction(client.GetMap<string, string>(map.GetName()), "key");
                }
                finally
                {
                    client.Shutdown();
                }

                TestSupport.AssertTrueEventually(() =>
                {
                    Assert.IsFalse(clientNearCache.Cache.ContainsKey(ToKeyData("key")),
                        "key should have been invalidated");
                });
            }
            finally
            {
                map.Destroy();
            }
        }

        [Test]
        public void TestNearCache()
        {
            for (var i = 0; i < 100; i++)
            {
                _map.Put("key" + i, "value" + i);
            }
            var begin = Clock.CurrentTimeMillis();
            for (var i = 0; i < 100; i++)
            {
                _map.Get("key" + i);
            }
            var firstRead = Clock.CurrentTimeMillis() - begin;
            begin = Clock.CurrentTimeMillis();
            for (var i = 0; i < 100; i++)
            {
                _map.Get("key" + i);
            }
            var secondRead = Clock.CurrentTimeMillis() - begin;
            Assert.IsTrue(secondRead < firstRead);
        }

        [Test]
        public void TestNearCacheContains()
        {
            _map.Put("key", "value");

            _map.Get("key");
            _map.Get("invalidKey");
            Assert.IsTrue(_map.ContainsKey("key"));
            Assert.IsFalse(_map.ContainsKey("invalidKey"));
        }

        [Test]
        public void TestNearCacheGet()
        {
            _map.Put("key", "value");
            _map.Get("key");

            var clientNearCache = GetNearCache(_map);

            Assert.AreEqual("value", clientNearCache.Get(ToKeyData("key")));
        }

        [Test]
        public void TestNearCacheGetAll()
        {
            var keys = new List<object>();
            for (var i = 0; i < 100; i++)
            {
                _map.Put("key" + i, "value" + i);
                keys.Add("key" + i);
            }

            _map.GetAll(keys);

            var clientNearCache = GetNearCache(_map);

            Assert.AreEqual(100, clientNearCache.Cache.Count);
        }

        [Test]
        public void TestNearCacheGetAsync()
        {
            _map.Put("key", "value");

            var val = _map.GetAsync("key");

            var result = val.Result;
            Assert.AreEqual("value", result);

            var clientNearCache = GetNearCache(_map);

            TestSupport.AssertTrueEventually(() => { Assert.AreEqual(1, clientNearCache.Cache.Count); });

            val = _map.GetAsync("key");
            result = val.Result;
            Assert.AreEqual("value", result);

            var cacheRecord = clientNearCache.Cache.Values.ToArray()[0];
            Assert.AreEqual(1, cacheRecord.Hit.Get());
        }

        [Test]
        public void TestNearCacheIdleEviction()
        {
            var map = Client.GetMap<int, int>("nearCacheIdle-" + TestSupport.RandomString());
            var keys = Enumerable.Range(0, 10).ToList();
            foreach (var k in keys)
            {
                map.Put(k, k);
            }
            var nonIdleKey = 100;
            map.Put(nonIdleKey, nonIdleKey);

            map.GetAll(keys);
            var cache = GetNearCache(map);

            Assert.AreEqual(keys.Count, cache.Cache.Count);
            TestSupport.AssertTrueEventually(() =>
            {
                map.Get(nonIdleKey); //force ttl check 
                foreach (var k in keys)
                {
                    var keyData = ToKeyData(k);
                    Assert.IsFalse(cache.Cache.ContainsKey(keyData), "key " + k + " should have expired.");
                }
                var nonIdleKeyData = ToKeyData(nonIdleKey);
                Assert.IsTrue(cache.Cache.ContainsKey(nonIdleKeyData), "key 100 should not have expired.");
            });
        }

        [Test]
        public void TestNearCacheInvalidationOnClear()
        {
            TestInvalidate((m, k) => m.Clear());
        }

        [Test]
        public void TestNearCacheInvalidationOnEvict()
        {
            TestInvalidate((m, k) => m.EvictAll());
        }

        [Test]
        public void TestNearCacheInvalidationOnPut()
        {
            TestInvalidate((m, k) => m.Remove(k));
        }

        [Test]
        public void TestNearCacheInvalidationOnRemove()
        {
            TestInvalidate((m, k) => m.Remove(k));
        }

        [Test]
        public void TestNearCacheLfuEviction()
        {
            var lfuMap = Client.GetMap<object, object>("nearCacheMapLfu-" + TestSupport.RandomString());
            var keys = new List<object>();
            for (var i = 0; i < MaxSize; i++)
            {
                lfuMap.Put(i, i);
                keys.Add(i);
            }

            // make sure all keys are cached
            lfuMap.GetAll(keys);

            // make keys in sublist accessed again
            var subList = keys.Take(MaxSize/2).ToList();
            lfuMap.GetAll(subList);

            // add another item, triggering eviction
            lfuMap.Put(MaxSize, MaxSize);
            lfuMap.Get(MaxSize);

            var cache = GetNearCache(lfuMap);

            TestSupport.AssertTrueEventually(() =>
            {
                Assert.IsTrue(cache.Cache.Count <= MaxSize, cache.Cache.Count + " should be less than " + MaxSize);
                foreach (var key in subList)
                {
                    var keyData = ((HazelcastClientProxy) Client).GetClient().GetSerializationService().ToData(key);
                    Assert.IsTrue(cache.Cache.ContainsKey(keyData), "key " + key + " not found in cache");
                }
            });
        }

        [Test]
        public void TestNearCacheLocalInvalidations()
        {
            var map = Client.GetMap<string, string>("nearCacheMapInvalidate-" + TestSupport.RandomString());
            map.Put("key", "value");

            var val = map.Get("key");
            Assert.AreEqual("value", val);

            var clientNearCache = GetNearCache(map);

            Assert.AreEqual(1, clientNearCache.Cache.Count);

            map.Remove("key");
            Assert.Null(map.Get("key"));
        }

        [Test]
        public void TestNearCacheLruEviction()
        {
            var lruMap = Client.GetMap<object, object>("nearCacheMapLru-" + TestSupport.RandomString());
            var keys = new List<object>();
            for (var i = 0; i < MaxSize; i++)
            {
                lruMap.Put(i, i);
                keys.Add(i);
            }

            // make sure all keys are cached
            lruMap.GetAll(keys);

            // make keys in sublist accessed again
            var subList = keys.Take(MaxSize/2).ToList();
            lruMap.GetAll(subList);

            // add another item, triggering eviction
            lruMap.Put(MaxSize, MaxSize);
            lruMap.Get(MaxSize);

            var cache = GetNearCache(lruMap);

            TestSupport.AssertTrueEventually(() =>
            {
                Assert.IsTrue(cache.Cache.Count <= MaxSize, cache.Cache.Count + " should be less than " + MaxSize);
                foreach (var key in subList)
                {
                    var keyData = ((HazelcastClientProxy) Client).GetClient().GetSerializationService().ToData(key);
                    Assert.IsTrue(cache.Cache.ContainsKey(keyData), "key " + key + " not found in cache");
                }
            });
        }

        [Test]
        public void TestNearCacheNoEviction()
        {
            var keys = new List<object>();
            for (var i = 0; i < MaxSize*2; i++)
            {
                _map.Put(i, i);
                keys.Add(i);
            }
            _map.GetAll(keys);
            var cache = GetNearCache(_map);
            Assert.AreEqual(MaxSize, cache.Cache.Count);
        }

        [Test]
        public void TestNearCacheTtlEviction()
        {
            var map = Client.GetMap<int, int>("nearCacheTtl-" + TestSupport.RandomString());
            var keys = Enumerable.Range(0, 10).ToList();
            foreach (var k in keys)
            {
                map.Put(k, k);
            }

            map.GetAll(keys);
            var cache = GetNearCache(map);

            Assert.AreEqual(keys.Count, cache.Cache.Count);
            TestSupport.AssertTrueEventually(() =>
            {
                map.Get(100); //force ttl check 
                foreach (var k in keys)
                {
                    var keyData = ((HazelcastClientProxy) Client).GetClient().GetSerializationService().ToData(k);
                    Assert.IsFalse(cache.Cache.ContainsKey(keyData), "key " + k + " should have expired.");
                }
            });
        }
    }
}