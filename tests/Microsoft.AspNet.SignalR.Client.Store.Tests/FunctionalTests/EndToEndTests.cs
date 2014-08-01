﻿
using System.IO;
using System.Text;
using Microsoft.AspNet.SignalR.Client.Transports;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.AspNet.SignalR.Client.Store.Tests
{
    // To run these tests you need to start Microsoft.AspNet.SignalR.Client.Store.TestHost first
    public class EndToEndTests
    {
        private const string HubUrl = "http://localhost:42424";
        //[Fact]
        public async Task WebSocketSendReceiveTest()
        {
            const int MessageCount = 3;
            var sentMessages = new List<string>();
            var receivedMessages = new List<string>();

            using (var hubConnection = new HubConnection(HubUrl))
            {
                var wh = new ManualResetEventSlim();

                var proxy = hubConnection.CreateHubProxy("StoreWebSocketTestHub");
                proxy.On<string>("echo", m =>
                {
                    receivedMessages.Add(m);
                    if (receivedMessages.Count == MessageCount)
                    {
                        wh.Set();
                    }
                });

                await hubConnection.Start(new WebSocketTransport());

                for (var i = 0; i < MessageCount; i++)
                {
                    var message = "MyMessage" + i;
                    await proxy.Invoke("Echo", message);
                    sentMessages.Add(message);
                }

                await Task.Run(() => wh.Wait(5000));
            }

            Assert.Equal(sentMessages, receivedMessages);
        }

        [Fact]
        public async Task WebSocketReconnects()
        {
            var stringBuilder = new StringBuilder();

            for (var i = 0; i < 100; i++)
            {
                stringBuilder.Length = 0;
                stringBuilder.Append("Iteration: ").AppendLine(i.ToString());

                try
                {
                    var stateChanges = new List<KeyValuePair<ConnectionState, ConnectionState>>();

                    using (var hubConnection = new HubConnection(HubUrl))
                    {
                        hubConnection.TraceLevel = TraceLevels.All;
                        hubConnection.TraceWriter = new StringWriter(stringBuilder);

                        string receivedMessage = null;
                        var messageReceivedWh = new ManualResetEventSlim();

                        var proxy = hubConnection.CreateHubProxy("StoreWebSocketTestHub");
                        proxy.On<string>("echo", m =>
                        {
                            stringBuilder.AppendLine("proxy.On: " + m);
                            receivedMessage = m;
                            messageReceivedWh.Set();
                        });

                        hubConnection.StateChanged += stateChanged => stateChanges.Add(
                            new KeyValuePair<ConnectionState, ConnectionState>(stateChanged.OldState,
                                stateChanged.NewState));

                        var reconnectingInvoked = false;
                        hubConnection.Reconnecting += () =>
                        {
                            stringBuilder.AppendLine("In Reconnecting event handler");
                            reconnectingInvoked = true;
                        };
                            

                        var reconnectedWh = new ManualResetEventSlim();
                        hubConnection.Reconnected += () =>
                        {
                            stringBuilder.AppendLine("In Reconnected event handler.");
                            reconnectedWh.Set();
                        };

                        await hubConnection.Start(new WebSocketTransport {ReconnectDelay = new TimeSpan(0, 0, 0, 500)});

                        stringBuilder.AppendLine("Connection started");

                        try
                        {
                            await proxy.Invoke("ForceReconnect");
                            stringBuilder.AppendLine("Should never get here.");
                        }
                        catch (InvalidOperationException)
                        {
                            stringBuilder.AppendLine("In exception handler");
                        }

                        stringBuilder.AppendLine("waiting for reconnected event");
                        Assert.True(await Task.Run(() => reconnectedWh.Wait(5000)));
                        
                        stringBuilder.AppendLine("Asserts");
                        Assert.True(reconnectingInvoked);
                        Assert.Equal(ConnectionState.Connected, hubConnection.State);

                        stringBuilder.AppendLine("Invoking Echo");
                        Assert.True(Task.Run(async () => await proxy.Invoke("Echo", "MyMessage" + Guid.NewGuid())).Wait(10000));

                        stringBuilder.AppendLine("Waiting for message received");
                        await Task.Run(() => messageReceivedWh.Wait(5000));

                        stringBuilder.AppendLine("Almost done");
                        Assert.StartsWith("MyMessage", receivedMessage);
                    }

                    Assert.Equal(
                        new[]
                        {
                            new KeyValuePair<ConnectionState, ConnectionState>(ConnectionState.Disconnected,
                                ConnectionState.Connecting),
                            new KeyValuePair<ConnectionState, ConnectionState>(ConnectionState.Connecting,
                                ConnectionState.Connected),
                            new KeyValuePair<ConnectionState, ConnectionState>(ConnectionState.Connected,
                                ConnectionState.Reconnecting),
                            new KeyValuePair<ConnectionState, ConnectionState>(ConnectionState.Reconnecting,
                                ConnectionState.Connected),
                            new KeyValuePair<ConnectionState, ConnectionState>(ConnectionState.Connected,
                                ConnectionState.Disconnected),
                        },
                        stateChanges);
                }
                catch (Exception ex)
                {
                    throw new Exception(stringBuilder.ToString(), ex);
                }
            }

            throw new Exception(stringBuilder.ToString());
        }

        //[Fact]
        public async Task WebSocketReconnectsIfConnectionLost()
        {
            var receivedMessage = (string)null;

            using (var hubConnection = new HubConnection(HubUrl))
            {
                hubConnection.StateChanged += stateChange =>
                {
                    if (stateChange.OldState == ConnectionState.Connected &&
                        stateChange.NewState == ConnectionState.Reconnecting)
                    {
                        // Reverting quick timeout 
                        ((IConnection) hubConnection).KeepAliveData = new KeepAliveData(
                            timeoutWarning: TimeSpan.FromSeconds(30),
                            timeout: TimeSpan.FromSeconds(20),
                            checkInterval: TimeSpan.FromSeconds(2));
                    }
                };

                var reconnectedWh = new ManualResetEventSlim();
                hubConnection.Reconnected += reconnectedWh.Set;

                var messageReceivedWh = new ManualResetEventSlim();
                var proxy = hubConnection.CreateHubProxy("StoreWebSocketTestHub");
                proxy.On<string>("echo", m =>
                {
                    receivedMessage = m;
                    messageReceivedWh.Set();
                });

                await hubConnection.Start(new WebSocketTransport { ReconnectDelay = new TimeSpan(0, 0, 0, 500) });

                // Setting the values such that a timeout happens almost instantly
                ((IConnection)hubConnection).KeepAliveData = new KeepAliveData(
                    timeoutWarning: TimeSpan.FromSeconds(10),
                    timeout: TimeSpan.FromSeconds(0.5),
                    checkInterval: TimeSpan.FromSeconds(1)
                );

                Assert.True(await Task.Run(() => reconnectedWh.Wait(5000)));

                await proxy.Invoke("Echo", "MyMessage");

                Assert.True(await Task.Run(() => messageReceivedWh.Wait(5000)));
                Assert.Equal("MyMessage", receivedMessage);
            }
        }
    }
}
