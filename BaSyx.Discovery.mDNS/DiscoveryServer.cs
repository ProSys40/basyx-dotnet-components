﻿/*******************************************************************************
* Copyright (c) 2020, 2021 Robert Bosch GmbH
* Author: Constantin Ziesche (constantin.ziesche@bosch.com)
*
* This program and the accompanying materials are made available under the
* terms of the Eclipse Public License 2.0 which is available at
* http://www.eclipse.org/legal/epl-2.0
*
* SPDX-License-Identifier: EPL-2.0
*******************************************************************************/
using Makaretu.Dns;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace BaSyx.Discovery.mDNS
{
    public class DiscoveryServer
    {
        private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

        private const int DISCOVER_THREAD_DELAY = 10000;

        public event EventHandler<ServiceDiscoveredEventArgs> ServiceDiscovered;
        public event EventHandler<ServiceInstanceEventArgs> ServiceInstanceDiscovered;
        public event EventHandler<ServiceInstanceEventArgs> ServiceInstanceShutdown;

        public string ServiceType { get; }

        private CancellationTokenSource cancellationToken;
        private Task discoverTask;
        private readonly MulticastService mdns;

        public DiscoveryServer(string serviceType)
        {
            ServiceType = serviceType;
            mdns = new MulticastService();
        }

        private async void Discover()
        {
            ServiceDiscovery sd = new ServiceDiscovery(mdns);

            mdns.NetworkInterfaceDiscovered += (s, e) =>
            {
                foreach (var networkInterface in e.NetworkInterfaces)
                {
                    logger.Info($"Network-Interface: '{networkInterface.Name}' found");
                }
            };

            sd.ServiceDiscovered += Sd_ServiceDiscovered;
            sd.ServiceInstanceDiscovered += Sd_ServiceInstanceDiscovered;
            sd.ServiceInstanceShutdown += Sd_ServiceInstanceShutdown;

            try
            {
                mdns.Start();

                cancellationToken = new CancellationTokenSource();
                while (!cancellationToken.IsCancellationRequested)
                {
                    sd.QueryAllServices();
                    await Task.Delay(DISCOVER_THREAD_DELAY).ConfigureAwait(false);
                }
            }                
            finally
            {
                sd.Dispose();
                mdns.Stop();
            }
        }

       
        private void Sd_ServiceDiscovered(object sender, DomainName serviceName)
        {
            logger.Info($"service '{serviceName}' discovered");

            ServiceDiscovered?.Invoke(sender, new ServiceDiscoveredEventArgs() { ServiceName = serviceName.ToString() });
            
            if(serviceName.ToString().Contains(ServiceType))
                mdns.SendQuery(serviceName, type: DnsType.PTR);
        }

        private void Sd_ServiceInstanceDiscovered(object sender, ServiceInstanceDiscoveryEventArgs e)
        {
            logger.Info($"service instance '{e.ServiceInstanceName}' discovered");

            if (e.ServiceInstanceName.ToString().Contains(ServiceType))
            {
                ServiceInstanceEventArgs args = GetServiceInstanceEventArgs(e, e.ServiceInstanceName.ToString());
                ServiceInstanceDiscovered?.Invoke(sender, args);
            }
        }

        private void Sd_ServiceInstanceShutdown(object sender, ServiceInstanceShutdownEventArgs e)
        {
            logger.Info($"service instance '{e.ServiceInstanceName}' is shutting down");

            var args = GetServiceInstanceEventArgs(e, e.ServiceInstanceName.ToString());
            ServiceInstanceShutdown?.Invoke(sender, args);
        }

        private ServiceInstanceEventArgs GetServiceInstanceEventArgs(MessageEventArgs e, string serviceName)
        {
            var servers = e.Message.AdditionalRecords.OfType<SRVRecord>();
            var addresses = e.Message.AdditionalRecords.OfType<AddressRecord>();
            var txtRecords = e.Message.AdditionalRecords.OfType<TXTRecord>()?.SelectMany(s => s.Strings);

            ServiceInstanceEventArgs args = new ServiceInstanceEventArgs() { ServiceName = serviceName };
            if (txtRecords?.Count() > 0)
                args.TxtRecords.AddRange(txtRecords);

            if (servers?.Count() > 0 && addresses?.Count() > 0)
            {
                foreach (var server in servers)
                {
                    logger.Info($"host '{server.Target}' for '{server.Name}' at port '{server.Port}'");
                    var serverAddresses = addresses.Where(w => w.Name == server.Target);
                    if (serverAddresses?.Count() > 0)
                    {
                        foreach (var serverAddress in serverAddresses)
                        {
                            logger.Info($"host '{serverAddress.Name}' at {serverAddress.Address}");
                            args.Servers.Add(new Server()
                            {
                                Name = server.Name.ToString(),
                                Target = server.Target.ToString(),
                                Port = server.Port,
                                Address = serverAddress.Address
                            });
                        }
                        return args;
                    }
                }
            }
            return args;
        }
        
        public void Start()
        {
            logger.Info("Discovery task starting...");
            cancellationToken = new CancellationTokenSource();
            discoverTask = Task.Factory.StartNew(() => Discover(), cancellationToken.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            logger.Info("Discovery task started successfully" );
        }


        public async void Stop()
        {
            logger.Info("Discovery task stopping...");
            cancellationToken?.Cancel();

            if (await Task.WhenAny(discoverTask, Task.Delay(DISCOVER_THREAD_DELAY + 500)) == discoverTask)
                logger.Info("Discovery task stopped successfully");
            else
                logger.Warn("Failed to stop discovery task - Timeout");
        }
    }

    public class ServiceInstanceEventArgs
    {
        public List<Server> Servers { get; set; }
        public List<string> TxtRecords { get; set; }
        public string ServiceName { get; set; }

        public ServiceInstanceEventArgs()
        {
            Servers = new List<Server>();
            TxtRecords = new List<string>();
        }
    }

    public class Server
    {
        public IPAddress Address { get; set; }
        public int Port { get; set; }
        public string Target { get; set; }
        public string Name { get; set; }
    }

    public class ServiceDiscoveredEventArgs
    {
        public string ServiceName { get; internal set; }
    }
}
