﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using SQLite;
using bitmessage.network;

namespace bitmessage
{
    public class Bitmessage:IDisposable
    {
		private TcpListener _listener;
		private readonly List<Client> _clients = new List<Client>(8);
		internal MemoryInventory MemoryInventory;

		private readonly Thread _listenerLoopThread;
		private readonly Thread _clientsControlThread;

		private readonly int _port;
		private readonly string _baseName;
	    internal readonly SQLiteAsyncConnection DB;

		public Bitmessage(string baseName = "bitmessage.sqlite", int port = 8444)
		{
			_baseName = baseName;
			_port = port;

			bool newDBFile = !File.Exists(baseName);
			DB = new SQLiteAsyncConnection(_baseName);

			if (newDBFile)
			{
				DB.CreateTableAsync<Node>().Wait();
				DB.CreateTableAsync<Payload>().Wait();
				DB.CreateTableAsync<Pubkey>().Wait();
				DB.CreateTableAsync<PrivateKey>().Wait();
				DB.CreateTableAsync<Broadcast>().Wait();

				DB.InsertAsync(new Node("127.0.0.1", 8444)).Wait();

				//db.InsertAsync(new Node("80.69.173.220", 443)).Wait();
				//db.InsertAsync(new Node("109.95.105.15", 8443)).Wait();
				//db.InsertAsync(new Node("66.65.120.151", 8080)).Wait();
				//db.InsertAsync(new Node("76.180.233.38", 8444)).Wait();
				//db.InsertAsync(new Node("84.48.88.42", 443)).Wait();
				//db.InsertAsync(new Node("74.132.73.137", 443)).Wait();
				//db.InsertAsync(new Node("60.242.109.18", 443)).Wait();
			}

			MemoryInventory = new MemoryInventory(DB);

			_listener = new TcpListener(IPAddress.Any, _port);
			try
			{
				_listener.Start();
			}
			catch (Exception e)
			{
				Debug.WriteLine("Порт занят? "+e);
				Debug.WriteLine("Пытаюсь стартовать прослушивание на другом порту");
				_listener = new TcpListener(IPAddress.Any, 0);
				_listener.Start();
				Debug.WriteLine("Сервер стартовал нормально");
			}

			_listenerLoopThread = new Thread(ListenerLoop) { IsBackground = true, Name = "ListenerLoop Server" };
			_listenerLoopThread.Start();

			_clientsControlThread = new Thread(ClientsControlLoop) { IsBackground = true, Name = "ClientsControlLoop" };
			_clientsControlThread.Start();
		}

	    #region Dispose

		private bool _disposed;

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!_disposed)
			{
				if (_listener != null)
					_listener.Stop();
				_listener = null;

				foreach (Client client in _clients)
					client.Stop();
				
				_disposed = true;
			}
		}

		~Bitmessage() { Dispose(false); }

		#endregion Dispose

		private void ListenerLoop()
		{
			while (_listener != null)
			{
				Client incoming = new Client(this, _listener.AcceptTcpClient());
				Debug.WriteLine("Подключение к серверу c " + incoming.Node.HostStreamPort);

				foreach (var client in _clients)
					if (client.Node.HostStreamPort == incoming.Node.HostStreamPort)
						client.Stop();

				_clients.Add(incoming);

				incoming.Listen();
			}
		}

		private void ClientsControlLoop()
		{
			// проверяю _listener, т.к. по такому же условию заканчивается цикл ListenerLoop
			while (_listener != null)
			{
				// Удаляю неподключенные клиенты
				for (int i = _clients.Count - 1; i >= 0; --i)
					if (!_clients[i].Connected)
						_clients.RemoveAt(i);
				// Довожу количество подключенных клиентов до 8
				if (_clients.Count<8)
				{
					var task = DB.Table<Node>().OrderBy(x => x.NumberOfBadConnection).ToListAsync();
					task.Wait();
					var nodes = task.Result;
					foreach (Node node in nodes)
					{
						bool find = _clients.Any(c => c.Node.HostStreamPort == node.HostStreamPort);
						if(!find)
						{
							Client c = new Client(this, node);
							try
							{
								Debug.WriteLine("Пытаюсь подключиться к "+node.HostStreamPort);
								c.Connect();
							}
							catch(Exception e)
							{
								Debug.WriteLine(node.HostStreamPort + " " + e);
							}
							if (c.Connected)
							{
								Debug.WriteLine("Подключился к " + node.HostStreamPort);
								_clients.Add(c);
							}
							if (_clients.Count >= 8) break;
						}
					}
				}
				// Сплю минуту, или если уведомят из Helper.ReadHeaderMagic
				MaybeDisconnect.WaitOne(60*1000);
			}
		}

	    public AutoResetEvent MaybeDisconnect = new AutoResetEvent(false);

	    public event Payload.EventHandler   NewPayload;
		public event Broadcast.EventHandler ReceiveBroadcast;
		public event Broadcast.EventHandler ReceiveInvalidBroadcast;
		public event Pubkey.EventHandler    ReceivePubkey;
	    public event Pubkey.EventHandler    ReceiveInvalidPubkey;

		public void OnNewPayload(Payload payload)
		{
			Payload.EventHandler handler = NewPayload;
			if (handler != null) handler(payload);
		}

	    public void OnReceiveBroadcast(Broadcast broadcast)
	    {
		    Broadcast.EventHandler handler = ReceiveBroadcast;
		    if (handler != null) handler(broadcast);
	    }

		public void OnReceiveInvalidBroadcast(Broadcast broadcast)
		{
			Broadcast.EventHandler handler = ReceiveInvalidBroadcast;
			if (handler != null) handler(broadcast);
		}

		public void OnReceivePubkey(Pubkey pubkey)
		{
			Pubkey.EventHandler handler = ReceivePubkey;
			if (handler != null) handler(pubkey);
		}

		public void OnReceiveInvalidPubkey(Pubkey pubkey)
		{
			Pubkey.EventHandler handler = ReceiveInvalidPubkey;
			if (handler != null) handler(pubkey);
		}

	    #region see https://bitmessage.org/wiki/API_Reference

		public PrivateKey GeneratePrivateKey(string label, bool eighteenByteRipe = false)
		{
			PrivateKey privateKey = new PrivateKey(label, eighteenByteRipe);
			privateKey.SaveAsync(DB);
			return privateKey;
		}

		public IEnumerable<PrivateKey> ListAddresses()
		{
			return PrivateKey.GetAll(DB);
		}

		public PrivateKey CreateRandomAddress(string label, bool eighteenByteRipe = false)
		{
			PrivateKey privateKey = new PrivateKey(label, eighteenByteRipe);
			privateKey.SaveAsync(DB);
			return privateKey;
		}

	    public IEnumerable<Message> GetAllInboxMessages()
	    {
			throw new NotImplementedException();
	    }

		public void TrashMessage(string msgid)
		{
			throw new NotImplementedException();
		}

		public void SendMessage(Pubkey toAddress, Pubkey fromAddress, string subject, string message, int encodingType = 2)
		{
			throw new NotImplementedException();
		}

		public void SendBroadcast(string fromAddress, string subject, string body, int encodingType = 2)
		{
			Broadcast broadcast = new Broadcast(this)
			{
				Key = fromAddress,
				Body = body,
				Subject = subject
			};
			broadcast.SaveAsync(DB);
			broadcast.Send();
		}

		#endregion see https://bitmessage.org/wiki/API_Reference
    }
}