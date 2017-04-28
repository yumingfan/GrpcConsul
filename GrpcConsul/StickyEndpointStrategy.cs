﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Grpc.Core;

namespace GrpcConsul
{
    public class StickyEndpointStrategy : IEndpointStrategy
    {
        private readonly ServiceDiscovery _serviceDiscovery;
        private readonly ConcurrentDictionary<string, DefaultCallInvoker> _invokers = new ConcurrentDictionary<string, DefaultCallInvoker>();

        private readonly object _lock = new object();
        private readonly Dictionary<string, Channel> _channels = new Dictionary<string, Channel>();

        public StickyEndpointStrategy(ServiceDiscovery serviceDiscovery)
        {
            _serviceDiscovery = serviceDiscovery;
        }

        public CallInvoker Get(string serviceName)
        {
            // find callInvoker first if any (fast path)
            if (_invokers.TryGetValue(serviceName, out var callInvoker))
            {
                return callInvoker;
            }

            // no luck (slow path): either no call invoker available or a shutdown is in progress
            lock (_lock)
            {
                // this is double-check lock
                if (_invokers.TryGetValue(serviceName, out callInvoker))
                {
                    return callInvoker;
                }

                // find a (shared) channel for target if any
                var target = _serviceDiscovery.FindServiceEndpoint(serviceName);
                if (!_channels.TryGetValue(target, out var channel))
                {
                    channel = new Channel(target, ChannelCredentials.Insecure);
                    _channels.Add(target, channel);
                }

                // build a new call invoker + channel
                callInvoker = new DefaultCallInvoker(channel);
                _invokers.TryAdd(serviceName, callInvoker);

                return callInvoker;
            }
        }

        public void Revoke(string serviceName, CallInvoker failedCallInvoker)
        {
            lock (_lock)
            {
                // only destroy the call invoker if & only if it is still published (first arrived wins)
                if (!_invokers.TryGetValue(serviceName, out var callInvoker) || !ReferenceEquals(callInvoker, failedCallInvoker))
                {
                    return;
                }
                _invokers.TryRemove(serviceName, out callInvoker);

                // a bit hackish
                var channelFieldInfo = failedCallInvoker.GetType().GetField("channel", BindingFlags.GetField | BindingFlags.NonPublic | BindingFlags.Instance);
                var failedChannel = (Channel) channelFieldInfo.GetValue(failedCallInvoker);

                // shutdown the channel
                if(_channels.TryGetValue(failedChannel.Target, out var channel) && ReferenceEquals(channel, failedChannel))
                {
                    _channels.Remove(failedChannel.Target);
                    _serviceDiscovery.Blacklist(failedChannel.Target);
                }

                failedChannel.ShutdownAsync();
            }
        }
    }
}