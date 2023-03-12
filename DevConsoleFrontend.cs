// SPDX-FileCopyrightText: 2023 Admer Šuko
// SPDX-License-Identifier: MIT

using Elegy.Utilities;
using Elegy.Utilities.Interfaces;
using System.Text;
using ENetConnection = Godot.ENetConnection;

namespace Elegy.DevConsole
{
	internal sealed class ConsoleMessage
	{
		//public byte[] Message { get; set; } = Array.Empty<byte>();
		public string Message { get; set; } = string.Empty;
		public ConsoleMessageType Type { get; set; } = ConsoleMessageType.Info;
		public float TimeSubmitted { get; set; } = 0.0f;
	}

	internal sealed class ThreadData
	{
		public enum ConnectionMode
		{
			Idle,
			Active,
			ShuttingDown
		}

		// If true, main thread will not modify MessageQueue
		// Controlled by the connection thread
		public bool IsTransmittingMessages { get; set; }

		// If false, main thread can safely modify MessageQueue
		// Controlled by the main thread
		public bool CanTransmitMessages { get; set; }

		public List<ConsoleMessage> MessageQueue { get; set; } = new();

		public ConnectionMode Mode { get; set; } = ConnectionMode.Idle;
	}

	public class DevConsoleFrontend : IConsoleFrontend
	{
		public string Name => "Elegy External Developer Console";

		public string Error { get; set; } = string.Empty;

		public bool Initialised { get; set; } = false;

		public DevConsoleFrontend()
		{
			mConnectionThread = new Thread( ConnectionThread );
		}

		public bool Init()
		{
			mConnectionThread.Start();

			// In the future, in order to support logging from multiple engine instances, we might wanna have the ability to choose
			// different ports. E.g. a port range from 23005 to 23015. Also, remote logging possibilities???
			if ( mConnection.CreateHostBound( bindAddress: "127.0.0.1", bindPort: 23005, maxPeers: 4, maxChannels: 1 ) != Godot.Error.Ok )
			{
				Error = "Failed to create host, app won't be able to connect to this engine instance";
				return false;
			}

			Console.Log( "[DevConsole] Bridge has been established!", ConsoleMessageType.Developer );

			mThreadData.Mode = ThreadData.ConnectionMode.Active;

			Initialised = true;
			return true;
		}

		public void OnLog( string message, ConsoleMessageType type, float timeSubmitted )
		{
			mMessagesToAdd.Add( new()
			{
				//Message = Encoding.ASCII.GetBytes( message ),
				Message = message,
				TimeSubmitted = timeSubmitted,
				Type = type
			} );
		}

		public void OnUpdate( float delta )
		{
			mTimeToNextTransmission -= delta;
			if ( mTimeToNextTransmission > 0.0f || mThreadData.IsTransmittingMessages )
			{
				return;
			}

			mThreadData.CanTransmitMessages = false;
			mThreadData.MessageQueue.AddRange( mMessagesToAdd );
			mMessagesToAdd.Clear();
			mThreadData.CanTransmitMessages = true;

			mTimeToNextTransmission = 0.1f;
		}

		private void LogEvent( Godot.Collections.Array? serviceResult )
		{
			if ( serviceResult == null )
			{
				return;
			}

			var eventType = serviceResult[0].As<ENetConnection.EventType>();
			var peer = serviceResult[1].As<Godot.ENetPacketPeer>();
			if ( eventType == ENetConnection.EventType.None || peer == null )
			{
				return;
			}

			if ( eventType == ENetConnection.EventType.Connect )
			{
				string peerString = $"'{peer.GetRemoteAddress()}:{peer.GetRemotePort()}'";
				mPeerMap[peer] = peerString;
				Console.Log( $"[DevConsole] Connection established! (from {peerString})", ConsoleMessageType.Developer );
			}
			else if ( eventType == ENetConnection.EventType.Disconnect )
			{
				Console.Log( $"[DevConsole] Connection terminated (with {mPeerMap[peer]})", ConsoleMessageType.Developer );
				mPeerMap.Remove( peer );
			}
		}

		private static byte[] EncodeMessage( string message, float time, ConsoleMessageType type )
		{
			ByteBuffer buffer = new( 1 + 1 + 4 + 2 + message.Length );
			// 1 byte for the packet type
			buffer.WriteChar( 'M' );
			// 1 byte for the console message type
			buffer.WriteEnum( type );
			// 4 bytes for the submission time
			buffer.WriteF32( time );
			// 2 + N bytes for string length and string contents
			buffer.WriteStringAscii( message, StringLength.Medium );

			return buffer.Data.ToArray();
		}

		private void ConnectionThread( object? obj )
		{
			bool keepRunning = true;
			while ( keepRunning )
			{
				switch ( mThreadData.Mode )
				{
					case ThreadData.ConnectionMode.Idle:
						{
							Thread.Sleep( 100 );
						} break;

					case ThreadData.ConnectionMode.Active:
						{
							if ( mThreadData.CanTransmitMessages && mThreadData.MessageQueue.Count > 0 )
							{
								mThreadData.IsTransmittingMessages = true;

								for ( int i = 0; i < mThreadData.MessageQueue.Count; i++ )
								{
									ConsoleMessage message = mThreadData.MessageQueue[i];
									mConnection.Broadcast( 0, EncodeMessage( message.Message, message.TimeSubmitted, message.Type ), (int)Godot.ENetPacketPeer.FlagReliable );
								}
								mThreadData.MessageQueue.Clear();

								mThreadData.IsTransmittingMessages = false;
							}

							while ( true )
							{
								var result = mConnection.Service();
								if ( result[0].AsInt32() <= (int)ENetConnection.EventType.None )
								{
									break;
								}
								
								LogEvent( result );
							}

							Thread.Sleep( 100 );
						} break;

					case ThreadData.ConnectionMode.ShuttingDown:
						{
							mConnection.Broadcast( 0, new byte[] { (byte)'X' }, (int)Godot.ENetPacketPeer.FlagReliable );
							for ( int i = 0; i < 64; i++ )
							{
								LogEvent( mConnection.Service( 1 ) );
							}

							Thread.Sleep( 50 );

							var peers = mConnection.GetPeers();
							foreach ( var peer in peers )
							{
								peer.PeerDisconnectNow();
							}
							for ( int i = 0; i < 64; i++ )
							{
								LogEvent( mConnection.Service( 1 ) );
							}

							mPeerMap.Clear();

							keepRunning = false;
						} break;
				}
			}
		}

		public void Shutdown()
		{
			Console.Log( $"[DevConsole] Shutdown" );
			Initialised = false;

			// Force send all messages
			// Very crude way of doing it, but it works
			for ( int i = 0; i < 15; i++ )
			{
				OnUpdate( 10.0f );
				Thread.Sleep( 10 );
			}

			mThreadData.Mode = ThreadData.ConnectionMode.ShuttingDown;
			mConnectionThread.Join();
		}

		private Dictionary<Godot.ENetPacketPeer, string> mPeerMap = new();
		private ENetConnection mConnection = new();
		private Thread mConnectionThread;
		private ThreadData mThreadData = new();
		private List<ConsoleMessage> mMessagesToAdd = new();
		private float mTimeToNextTransmission = 0.2f;
	}
}