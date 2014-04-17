﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading.Tasks;
using Espera.Core.Analytics;
using Espera.Core.Management;
using Rareform.Validation;
using ReactiveUI;

namespace Espera.Services
{
    /// <summary>
    /// Provides methods for connecting mobile endpoints with the application.
    /// </summary>
    public class MobileApi : IDisposable, IEnableLogger
    {
        private readonly object clientListGate;
        private readonly ReactiveList<MobileClient> clients;
        private readonly BehaviorSubject<bool> isPortOccupied;
        private readonly Library library;
        private readonly int port;
        private bool dispose;
        private TcpListener listener;
        private IDisposable listenerSubscription;

        public MobileApi(int port, Library library)
        {
            if (port < 49152 || port > 65535)
                Throw.ArgumentOutOfRangeException(() => port);

            if (library == null)
                Throw.ArgumentNullException(() => library);

            this.port = port;
            this.library = library;
            this.clients = new ReactiveList<MobileClient>();
            this.clientListGate = new object();
            this.isPortOccupied = new BehaviorSubject<bool>(false);
        }

        public IObservable<int> ConnectedClients
        {
            get { return this.clients.CountChanged; }
        }

        public IObservable<bool> IsPortOccupied
        {
            get { return this.isPortOccupied; }
        }

        public void Dispose()
        {
            this.Log().Info("Stopping to listen for incoming connections on port {0}", this.port);

            if (this.listenerSubscription != null)
            {
                this.listenerSubscription.Dispose();
            }

            this.dispose = true;
            this.listener.Stop();

            lock (this.clientListGate)
            {
                foreach (MobileClient client in clients)
                {
                    client.Dispose();
                }

                this.clients.Clear();
            }
        }

        public async Task SendBroadcastAsync()
        {
            byte[] message = Encoding.Unicode.GetBytes("espera-server-discovery");

            using (var client = new UdpClient(this.port))
            {
                while (!this.dispose)
                {
                    await client.SendAsync(message, message.Length, new IPEndPoint(IPAddress.Broadcast, this.port));

                    await Task.Delay(1000);
                }
            }
        }

        public void StartClientDiscovery()
        {
            this.listener = new TcpListener(new IPEndPoint(IPAddress.Any, this.port));
            this.Log().Info("Starting to listen for incoming connections on port {0}", this.port);

            try
            {
                listener.Start();
            }

            catch (SocketException ex)
            {
                this.Log().ErrorException(string.Format("Port {0} is already taken", this.port), ex);
                this.isPortOccupied.OnNext(true);
                return;
            }

            this.listenerSubscription = Observable.Defer(() => this.listener.AcceptTcpClientAsync().ToObservable())
                .Repeat()
                .Subscribe(socket =>
                {
                    this.Log().Info("New client detected");

                    AnalyticsClient.Instance.RecordMobileUsage();

                    var mobileClient = new MobileClient(socket, this.library);

                    mobileClient.Disconnected.FirstAsync()
                        .Subscribe(x =>
                        {
                            mobileClient.Dispose();

                            lock (this.clientListGate)
                            {
                                this.clients.Remove(mobileClient);
                            }
                        });

                    mobileClient.ListenAsync();

                    lock (this.clientListGate)
                    {
                        this.clients.Add(mobileClient);
                    }
                });
        }
    }
}