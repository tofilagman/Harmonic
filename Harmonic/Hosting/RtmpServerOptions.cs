﻿using Autofac;
using Harmonic.Controllers;
using Harmonic.Controllers.Living;
using Harmonic.Networking.Rtmp;
using Harmonic.Networking.Rtmp.Data;
using Harmonic.Networking.Rtmp.Messages;
using Harmonic.Networking.Rtmp.Messages.Commands;
using Harmonic.Networking.Rtmp.Messages.UserControlMessages;
using Harmonic.Networking.Rtmp.Serialization;
using Harmonic.NetWorking.Rtmp.Messages;
using Harmonic.Rpc;
using Harmonic.Service;
using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text;

namespace Harmonic.Hosting
{
    public class RtmpServerOptions
    {
        internal Dictionary<MessageType, MessageFactory> _messageFactories = new Dictionary<MessageType, MessageFactory>();
        public delegate Message MessageFactory(MessageHeader header, Networking.Rtmp.Serialization.SerializationContext context, out int consumed);
        private Dictionary<string, Type> _registeredControllers = new Dictionary<string, Type>();
        private ContainerBuilder _builder = null;
        private RpcService _rpcService = null;
        internal IStartup _startup = null;
        internal IStartup Startup
        {
            get
            {
                return _startup;
            }
            set
            {
                _startup = value;
                _builder = new ContainerBuilder();
                _startup.ConfigureServices(_builder);
                SessionScopedServices = new List<Type>(_startup.SessionScopedServices);
                RegisterCommonServices(_builder);
            }
        }
        public List<Type> SessionScopedServices { get; private set; }
        public IContainer ServiceContainer { get; private set; }
        public ILifetimeScope ServerLifetime { get; private set; }

        public IReadOnlyDictionary<string, Type> RegisteredControllers => _registeredControllers;
        public int RtmpPort { get; set; } = 1935;
        public IPAddress RtmpIPAddress { get; set; } = IPAddress.Any;
        public int WebsocketPort { get; set; } = 80;
        public IPAddress WebsocketIPAddress { get; set; } = IPAddress.Any;
        public bool UseUdp { get; set; } = true;
        public bool UseWebsocket { get; set; } = true;

        public RtmpServerOptions()
        {
            var userControlMessageFactory = new UserControlMessageFactory();
            var commandMessageFactory = new CommandMessageFactory();
            RegisterMessage<AbortMessage>();
            RegisterMessage<AcknowledgementMessage>();
            RegisterMessage<SetChunkSizeMessage>();
            RegisterMessage<SetPeerBandwidthMessage>();
            RegisterMessage<WindowAcknowledgementSizeMessage>();
            RegisterMessage<DataMessage>((MessageHeader header, SerializationContext context, out int consumed) =>
            {
                consumed = 0;
                return new DataMessage(header.MessageType == MessageType.Amf0Data ? AmfEncodingVersion.Amf0 : AmfEncodingVersion.Amf3);
            });
            RegisterMessage<VideoMessage>();
            RegisterMessage<AudioMessage>();
            RegisterMessage<UserControlMessage>(userControlMessageFactory.Provide);
            RegisterMessage<CommandMessage>(commandMessageFactory.Provide);
            _rpcService = new RpcService();
        }

        internal void BuildContainer()
        {
            ServiceContainer = _builder.Build();
            ServerLifetime = ServiceContainer.BeginLifetimeScope();
        }

        public void RegisterMessage<T>(MessageFactory factory) where T : Message
        {
            var tType = typeof(T);
            var attr = tType.GetCustomAttribute<RtmpMessageAttribute>();
            if (attr == null)
            {
                throw new InvalidOperationException();
            }

            foreach (var messageType in attr.MessageTypes)
            {
                _messageFactories.Add(messageType, factory);
            }
        }

        public void RegisterMessage<T>() where T : Message, new()
        {
            var tType = typeof(T);
            var attr = tType.GetCustomAttribute<RtmpMessageAttribute>();
            if (attr == null)
            {
                throw new InvalidOperationException();
            }

            foreach (var messageType in attr.MessageTypes)
            {
                _messageFactories.Add(messageType, (MessageHeader a, SerializationContext b, out int c) =>
                {
                    c = 0;
                    return new T();
                });
            }
        }

        public void RegisterController(Type controllerType, string appName = null)
        {
            if (!typeof(AbstractController).IsAssignableFrom(controllerType))
            {
                throw new InvalidOperationException("controllerType must inherit from AbstractController");
            }
            var name = appName ?? controllerType.Name.Replace("Controller", "");
            _registeredControllers.Add(name.ToLower(), controllerType);
            _rpcService.RegeisterController(controllerType);
            _builder.RegisterType(controllerType).AsSelf();
        }
        public void RegisterStream(Type streamType)
        {
            if (!typeof(NetStream).IsAssignableFrom(streamType))
            {
                throw new InvalidOperationException("streamType must inherit from NetStream");
            }
            _rpcService.RegeisterController(streamType);
            _builder.RegisterType(streamType).AsSelf();
        }
        private void RegisterCommonServices(ContainerBuilder builder)
        {
            builder.Register(c => new PublisherSessionService())
                .AsSelf()
                .InstancePerLifetimeScope();
            builder.Register(c => _rpcService)
                .AsSelf()
                .SingleInstance();

        }

        public void RegisterController<T>(string appName = null) where T : AbstractController
        {
            RegisterController(typeof(T), appName);
        }
        public void RegisterStream<T>() where T : NetStream
        {
            RegisterStream(typeof(T));
        }
    }
}