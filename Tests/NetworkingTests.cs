﻿using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using NUnit.Framework;

namespace SimMach.Sim {
    public sealed class NetworkingTests {
        [Test]
        public void RequestReplyTest() {
            var run = new TestRuntime() {
                MaxTime = TimeSpan.FromMinutes(2),
                DebugNetwork = true,
            };

            var requests = new List<object>();
            var responses = new List<object>();

            run.Net.Link("client", "server");

            run.Services.Add("client:service", async env => {
                using (var conn = await env.Connect("server", 80)) {
                    await conn.Write("Hello");
                    responses.Add(await conn.Read(5.Sec()));
                }
            });

            async void Handler(IConn conn) {
                using (conn) {
                    requests.Add(await conn.Read(5.Sec()));
                    await conn.Write("World");
                }
            }

            run.Services.Add("server:service", async env => {
                using (var sock = await env.Listen(80)) {
                    while (!env.Token.IsCancellationRequested) {
                        var conn = await sock.Accept();
                        Handler(conn);
                    }
                }
                
            });

            run.RunAll();

            CollectionAssert.AreEquivalent(new object[]{"Hello"}, requests);
            CollectionAssert.AreEquivalent(new object[]{"World"}, responses);
            
        }
        
        
        
        [Test]
        public void SubscribeTest() {
            var run = new TestRuntime() {
                MaxTime = 2.Minutes(),
                DebugNetwork = true,
            };


            int eventsReceived = 0;
            int eventsToSend = 5;
            bool closed = false;
            run.Net.Link("client", "server");

            run.Services.Add("client:service", async env => {
                using (var conn = await env.Connect("server", 80)) {
                    env.Debug("Subscribing");
                    await conn.Write("SUBSCRIBE");


                    while (!env.Token.IsCancellationRequested) {
                        var msg = await conn.Read(5.Sec());
                        if (msg == "END_STREAM") {
                            env.Debug("End of stream");
                            break;
                        }
                        env.Debug($"Got {msg}");
                        eventsReceived++;
                    }



                    closed = true;
                }
            });

            run.Services.Add("server:service", async env => {
                
                async void Handler(IConn conn) {
                    using (conn) {
                        await conn.Read(5.Sec());
                        for (int i = 0; i < eventsToSend; i++) {
                            await env.Delay(10.Ms(), env.Token);
                            await conn.Write("Event " + i);
                        }

                        await conn.Write("END_STREAM");
                    }
                }
                
                using (var sock = await env.Listen(80)) {
                    while (!env.Token.IsCancellationRequested) {
                        // TODO = cancel + timeout
                        var conn = await sock.Accept();
                        Handler(conn);
                    }
                }
            });

            run.RunAll();

            Assert.AreEqual(eventsToSend, eventsReceived);
            Assert.IsTrue(closed, nameof(closed));
        }
        
    }
}