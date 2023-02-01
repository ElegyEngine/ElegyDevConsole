// SPDX-FileCopyrightText: 2023 Admer Šuko
// SPDX-License-Identifier: MIT

using System.Text;
using ENetConnection = Godot.ENetConnection;

namespace Elegy.DevConsole
{
	public class DevConsoleFrontend : IConsoleFrontend
	{
		public string Name => "Elegy External Developer Console";

		public string Error { get; set; } = string.Empty;

		public bool Initialised { get; set; } = false;

		public bool Init()
		{
			// In the future, in order to support logging from multiple engine instances, we might wanna have the ability to choose
			// different ports. E.g. a port range from 23005 to 23015. Also, remote logging possibilities???
			if ( mConnection.CreateHostBound( bindAddress: "127.0.0.1", bindPort: 23005, maxPeers: 4, maxChannels: 1 ) != Godot.Error.Ok )
			{
				Error = "Failed to create host, app won't be able to connect to this engine instance";
				return false;
			}

			Console.Log( "[DevConsole] Bridge has been established!", ConsoleMessageType.Developer );

			Initialised = true;
			return true;
		}

		public void OnLog( string message, ConsoleMessageType type, float timeSubmitted )
		{
			// A little bit of filtering to ensure all we got are ASCII characters
			// The external app does not exactly like UTF-16...
			char[] chars = message.ToCharArray();
			for ( int i = 0; i < chars.Length; i++ )
			{
				if ( !char.IsAscii( chars[i] ) )
				{
					chars[i] = '?';
				}
			}
			message = new string( chars );

			mConnection.Broadcast( 0, EncodeMessage( message, type ), (int)Godot.ENetPacketPeer.FlagReliable );
		}

		private byte[] EncodeMessage( string message, ConsoleMessageType type )
		{
			// 1 byte for the network message type
			// 1 byte for the console message type
			// 2 bytes for the console message length
			// X bytes for the text
			List<byte> data = new( 1 + 1 + 2 + message.Length );
			data.Add( (byte)'M' );
			data.Add( (byte)type );
			data.Add( (byte)message.Length ); // we encode the 1st byte here
			data.Add( (byte)(message.Length >> 8) ); // and the 2nd byte here, making a short
			data.AddRange( Encoding.ASCII.GetBytes( message ) );

			return data.ToArray();
		}

		public void OnUpdate( float delta )
		{
			//mConnection.Flush();
			while ( true )
			{
				var result = mConnection.Service();
				if ( result[0].AsInt32() <= (int)ENetConnection.EventType.None )
				{
					break;
				}

				LogEvent( result );
			}
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

		public void Shutdown()
		{
			Console.Log( $"[DevConsole] Shutdown" );

			mPeerMap.Clear();

			mConnection.Broadcast( 0, new byte[] { (byte)'X' }, (int)Godot.ENetPacketPeer.FlagReliable );
			mConnection.Service();

			var peers = mConnection.GetPeers();
			foreach ( var peer in peers )
			{
				peer.PeerDisconnect();
			}
			for ( int i = 0; i < 50; i++ )
			{
				LogEvent( mConnection.Service() );
			}

			mConnection.Destroy();

			Initialised = false;
		}

		private Dictionary<Godot.ENetPacketPeer, string> mPeerMap = new();
		private ENetConnection mConnection = new();
	}
}