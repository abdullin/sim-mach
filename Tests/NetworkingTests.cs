﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using NUnit.Framework;

namespace SimMach.Sim {
    public sealed class NetworkingTests {

        [Test]
        public void NoRouteToHost() {
            var run = NewTestRuntime();
            
            var responses = new List<object>();
            AddHelloWorldClient(run, "server", responses);

            run.RunAll();
            AssertOneError(responses);
        }

        [Test]
        public void ConnectionRefused() {
            var run = NewTestRuntime();
            
            run.Net.Link("localhost", "server");
            
            var responses = new List<object>();
            AddHelloWorldClient(run, "server", responses);
            
            run.RunAll();
            
            AssertOneError(responses, "reset");
        }
        
        [Test]
        public void ServerTimeout() {
            var run = NewTestRuntime();

            var responses = new List<object>();
            run.Net.Link("localhost", "server");
            AddHelloWorldClient(run, "server", responses);
            
            run.Services.Add("server:dead", async env => {
                using (var socket = await env.Bind(80)) {
                    while (!env.Token.IsCancellationRequested) {
                        await env.Delay(10.Minutes());
                    }
                }
            });
            
            run.RunAll();
            
            AssertOneError(responses, "timeout");
        }

        static void AssertOneError(List<object> responses, string match = null) {
            Assert.AreEqual(1, responses.Count);
            Assert.IsInstanceOf<IOException>(responses.First());

            if (match != null) {
                var msg= responses.OfType<IOException>().First().Message;
                StringAssert.Contains(match, msg);
            }
        }

        [Test]
        public void RequestReply() {
            var run = NewTestRuntime();

            var requests = new List<object>();
            var responses = new List<object>();

            run.Net.Link("localhost", "server");
            
            AddHelloWorldClient(run, "server", responses);
            AddHelloWorldServer(run, "server", requests);

            run.RunAll();

            CollectionAssert.AreEquivalent(new object[]{"Hello"}, requests);
            CollectionAssert.AreEquivalent(new object[]{"World"}, responses);
        }
        
        [Test]
        public void RequestReplyThroughTheProxy() {
            var run = NewTestRuntime();
            var requests = new List<object>();
            var responses = new List<object>();

            run.Net.Link("localhost", "proxy");
            run.Net.Link("proxy", "server");

            AddHelloWorldClient(run, "proxy", responses);
            AddHelloWorldProxy(run, "proxy", "server");
            AddHelloWorldServer(run, "server", requests);

            run.RunAll();

            CollectionAssert.AreEquivalent(new object[]{"Hello"}, requests);
            CollectionAssert.AreEquivalent(new object[]{"World"}, responses);
        }

        static void AddHelloWorldServer(TestRuntime run, string endpoint, List<object> requests) {
            run.Services.Add(endpoint + ":engine", async env => {
                async void Handler(IConn conn) {
                    using (conn) {
                        var msg = await conn.Read(5.Sec());
                        requests.Add(msg);
                        await conn.Write("World");
                    }
                }

                using (var socket = await env.Bind(80)) {
                    while (!env.Token.IsCancellationRequested) {
                        Handler(await socket.Accept());
                    }
                }
            });
        }

       

        static void AddHelloWorldProxy(TestRuntime run, string endpoint, string target) {
            
            run.Services.Add(endpoint + ":engine", async env => {
            
                async void Handler(IConn conn) {
                    using (conn) {
                        var msg = await conn.Read(5.Sec());
                        using (var outgoing = await env.Connect(target, 80)) {
                            await outgoing.Write(msg);
                            var response = await outgoing.Read(5.Sec());
                            await conn.Write(response);
                        }
                    }
                }

                using (var socket = await env.Bind(80)) {
                    while (!env.Token.IsCancellationRequested) {
                        Handler(await socket.Accept());
                    }
                }

            });
        }

        static TestRuntime NewTestRuntime() {
            var run = new TestRuntime() {
                MaxTime = 2.Minutes(),
                DebugNetwork = true
            };
            return run;
        }

        static void AddHelloWorldClient(TestRuntime run, string endpoint, List<object> responses) {
            run.Services.Add("localhost:console", async env => {
                try {
                    using (var conn = await env.Connect(endpoint, 80)) {
                        await conn.Write("Hello");
                        var response = await conn.Read(5.Sec());
                        responses.Add(response);
                    }
                } catch (IOException ex) {
                    env.Debug(ex.Message);
                    responses.Add(ex);
                }
            });
        }


        [Test]
        public void SubscribeTest() {
            var run = NewTestRuntime();

            var eventsReceived = 0;
            var eventsToSend = 5;
            var closed = false;
            
            run.Net.Link("localhost", "api");
            run.Services.Add("localhost:console", async env => {
                using (var conn = await env.Connect("api", 80)) {
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

            run.Services.Add("api:engine", async env => {
                async void Handler(IConn conn) {
                    using (conn) {
                        await conn.Read(5.Sec());
                        for (var i = 0; i < eventsToSend; i++) {
                            await env.SimulateWork("work", 10.Ms());
                            await conn.Write($"Event {i}");
                        }
                        await conn.Write("END_STREAM");
                    }
                }
                
                using (var socket = await env.Bind(80)) {
                    while (!env.Token.IsCancellationRequested) {
                        var conn = await socket.Accept();
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