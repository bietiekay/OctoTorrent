//
// Mode.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2009 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

namespace OctoTorrent.Client
{
    using System;
    using System.Collections.Generic;
    using System.Configuration;
    using System.Linq;
    using Common;
    using Connections;
    using Encryption;
    using Messages;
    using Messages.FastPeer;
    using Messages.Libtorrent;
    using Messages.Standard;

    internal abstract class Mode
    {
        private readonly TorrentManager _manager;
        private int _webseedCount;
        private readonly int _addWebSeedsSpeedLimit;

        protected Mode(TorrentManager manager)
        {
            _addWebSeedsSpeedLimit = string.IsNullOrEmpty(ConfigurationManager.AppSettings["addWebSeedsSpeedLimit"])
                                         ? 0
                                         : int.Parse(ConfigurationManager.AppSettings["addWebSeedsSpeedLimit"]);

            CanAcceptConnections = true;
            _manager = manager;
            manager.ChokeUnchoker = new ChokeUnchokeManager(
                manager, manager.Settings.MinimumTimeBetweenReviews, manager.Settings.PercentOfMaxRateToSkipReview);
        }

        public abstract TorrentState State { get; }

        protected TorrentManager Manager
        {
            get { return _manager; }
        }

        public bool CanAcceptConnections { get; protected set; }

        public virtual bool CanHashCheck
        {
            get { return false; }
        }

        public void HandleMessage(PeerId id, PeerMessage message)
        {
            if (message is IFastPeerMessage && !id.SupportsFastPeer)
                throw new MessageException("Peer shouldn't support fast peer messages");

            if (message is ExtensionMessage && !id.SupportsLTMessages && !(message is ExtendedHandshakeMessage))
                throw new MessageException("Peer shouldn't support extension messages");

            if (message is HaveMessage)
                HandleHaveMessage(id, (HaveMessage) message);
            else if (message is RequestMessage)
                HandleRequestMessage(id, (RequestMessage) message);
            else if (message is PortMessage)
                HandlePortMessage(id, (PortMessage) message);
            else if (message is PieceMessage)
                HandlePieceMessage(id, (PieceMessage) message);
            else if (message is NotInterestedMessage)
                HandleNotInterested(id, (NotInterestedMessage) message);
            else if (message is KeepAliveMessage)
                HandleKeepAliveMessage(id, (KeepAliveMessage) message);
            else if (message is InterestedMessage)
                HandleInterestedMessage(id, (InterestedMessage) message);
            else if (message is ChokeMessage)
                HandleChokeMessage(id, (ChokeMessage) message);
            else if (message is CancelMessage)
                HandleCancelMessage(id, (CancelMessage) message);
            else if (message is BitfieldMessage)
                HandleBitfieldMessage(id, (BitfieldMessage) message);
            else if (message is UnchokeMessage)
                HandleUnchokeMessage(id, (UnchokeMessage) message);
            else if (message is HaveAllMessage)
                HandleHaveAllMessage(id, (HaveAllMessage) message);
            else if (message is HaveNoneMessage)
                HandleHaveNoneMessage(id, (HaveNoneMessage) message);
            else if (message is RejectRequestMessage)
                HandleRejectRequestMessage(id, (RejectRequestMessage) message);
            else if (message is SuggestPieceMessage)
                HandleSuggestedPieceMessage(id, (SuggestPieceMessage) message);
            else if (message is AllowedFastMessage)
                HandleAllowedFastMessage(id, (AllowedFastMessage) message);
            else if (message is ExtendedHandshakeMessage)
                HandleExtendedHandshakeMessage(id, (ExtendedHandshakeMessage) message);
            else if (message is LTMetadata)
                HandleLtMetadataMessage(id, (LTMetadata) message);
            else if (message is LTChat)
                HandleLtChat(id, (LTChat) message);
            else if (message is PeerExchangeMessage)
                HandlePeerExchangeMessage(id, (PeerExchangeMessage) message);
            else if (message is HandshakeMessage)
                HandleHandshakeMessage(id, (HandshakeMessage) message);
            else if (message is ExtensionMessage)
                HandleGenericExtensionMessage(id, (ExtensionMessage) message);
            else
                throw new MessageException(string.Format("Unsupported message found: {0}", message.GetType().Name));
        }

        public bool ShouldConnect(PeerId peer)
        {
            return ShouldConnect(peer.Peer);
        }

        public virtual bool ShouldConnect(Peer peer)
        {
            return true;
        }

        protected virtual void HandleGenericExtensionMessage(PeerId id, ExtensionMessage extensionMessage)
        {
            // Do nothing
        }

        protected virtual void HandleHandshakeMessage(PeerId id, HandshakeMessage message)
        {
            if (!message.ProtocolString.Equals(VersionInfo.ProtocolStringV100))
            {
                Logger.Log(id.Connection, "HandShake.Handle - Invalid protocol in handshake: {0}",
                           message.ProtocolString);
                throw new ProtocolException("Invalid protocol string");
            }

            // If we got the peer as a "compact" peer, then the peerid will be empty. In this case
            // we just copy the one that is in the handshake. 
            if (string.IsNullOrEmpty(id.Peer.PeerId))
                id.Peer.PeerId = message.PeerId;

            // If the infohash doesn't match, dump the connection
            if (message.InfoHash != id.TorrentManager.InfoHash)
            {
                Logger.Log(id.Connection, "HandShake.Handle - Invalid infohash");
                throw new TorrentException("Invalid infohash. Not tracking this torrent");
            }

            // If the peer id's don't match, dump the connection. This is due to peers faking usually
            if (id.Peer.PeerId != message.PeerId)
            {
                Logger.Log(id.Connection, "HandShake.Handle - Invalid peerid");
                throw new TorrentException("Supplied PeerID didn't match the one the tracker gave us");
            }

            // Attempt to parse the application that the peer is using
            id.ClientApp = new Software(message.PeerId);
            id.SupportsFastPeer = message.SupportsFastPeer;
            id.SupportsLTMessages = message.SupportsExtendedMessaging;

            // If they support fast peers, create their list of allowed pieces that they can request off me
            if (id.SupportsFastPeer && id.TorrentManager != null && id.TorrentManager.HasMetadata)
                id.AmAllowedFastPieces = AllowedFastAlgorithm.Calculate(id.AddressBytes, id.TorrentManager.InfoHash,
                                                                        (uint) id.TorrentManager.Torrent.Pieces.Count);
        }

        protected virtual void HandlePeerExchangeMessage(PeerId id, PeerExchangeMessage message)
        {
            // Ignore peer exchange messages on private torrents
            if (id.TorrentManager.Torrent.IsPrivate || !id.TorrentManager.Settings.EnablePeerExchange)
                return;

            // If we already have lots of peers, don't process the messages anymore.
            if ((Manager.Peers.AvailableCount + Manager.OpenConnections) >= _manager.Settings.MaxConnections)
                return;

            IList<Peer> peers = Peer.Decode(message.Added);
            var count = id.TorrentManager.AddPeersCore(peers);
            id.TorrentManager.RaisePeersFound(new PeerExchangePeersAdded(id.TorrentManager, count, peers.Count, id));
        }

        protected virtual void HandleLtChat(PeerId id, LTChat message)
        {
        }

        protected virtual void HandleLtMetadataMessage(PeerId id, LTMetadata message)
        {
            if (message.MetadataMessageType == LTMetadata.eMessageType.Request)
            {
                if (id.TorrentManager.HasMetadata)
                    id.Enqueue(new LTMetadata(id, LTMetadata.eMessageType.Data, message.Piece,
                                              id.TorrentManager.Torrent.Metadata));
                else
                    id.Enqueue(new LTMetadata(id, LTMetadata.eMessageType.Reject, message.Piece));
            }
        }

        protected virtual void HandleAllowedFastMessage(PeerId id, AllowedFastMessage message)
        {
            if (!Manager.Bitfield[message.PieceIndex])
                id.IsAllowedFastPieces.Add(message.PieceIndex);
        }

        protected virtual void HandleSuggestedPieceMessage(PeerId id, SuggestPieceMessage message)
        {
            id.SuggestedPieces.Add(message.PieceIndex);
        }

        protected virtual void HandleRejectRequestMessage(PeerId id, RejectRequestMessage message)
        {
            id.TorrentManager.PieceManager.Picker.CancelRequest(id, message.PieceIndex, message.StartOffset,
                                                                message.RequestLength);
        }

        protected virtual void HandleHaveNoneMessage(PeerId id, HaveNoneMessage message)
        {
            id.BitField.SetAll(false);
            id.Peer.IsSeeder = false;
            SetAmInterestedStatus(id, false);
        }

        protected virtual void HandleHaveAllMessage(PeerId id, HaveAllMessage message)
        {
            id.BitField.SetAll(true);
            id.Peer.IsSeeder = true;
            SetAmInterestedStatus(id, _manager.PieceManager.IsInteresting(id));
        }

        protected virtual void HandleUnchokeMessage(PeerId id, UnchokeMessage message)
        {
            id.IsChoking = false;

            // Add requests to the peers message queue
            _manager.PieceManager.AddPieceRequests(id);
        }

        protected virtual void HandleBitfieldMessage(PeerId id, BitfieldMessage message)
        {
            id.BitField = message.BitField;
            id.Peer.IsSeeder = (id.BitField.AllTrue);

            SetAmInterestedStatus(id, _manager.PieceManager.IsInteresting(id));
        }

        protected virtual void HandleCancelMessage(PeerId id, CancelMessage message)
        {
            for (var i = 0; i < id.QueueLength; i++)
            {
                var msg = id.Dequeue();
                if (!(msg is PieceMessage))
                {
                    id.Enqueue(msg);
                    continue;
                }

                var piece = msg as PieceMessage;
                if (
                    !(piece.PieceIndex == message.PieceIndex && piece.StartOffset == message.StartOffset &&
                      piece.RequestLength == message.RequestLength))
                {
                    id.Enqueue(msg);
                }
                else
                {
                    id.IsRequestingPiecesCount--;
                }
            }

            for (var i = 0; i < id.PieceReads.Count; i++)
            {
                if (id.PieceReads[i].PieceIndex == message.PieceIndex &&
                    id.PieceReads[i].StartOffset == message.StartOffset &&
                    id.PieceReads[i].RequestLength == message.RequestLength)
                {
                    id.IsRequestingPiecesCount--;
                    id.PieceReads.RemoveAt(i);
                    break;
                }
            }
        }

        protected virtual void HandleChokeMessage(PeerId id, ChokeMessage message)
        {
            id.IsChoking = true;
            if (!id.SupportsFastPeer)
                _manager.PieceManager.Picker.CancelRequests(id);
        }

        protected virtual void HandleInterestedMessage(PeerId id, InterestedMessage message)
        {
            id.IsInterested = true;
        }

        protected virtual void HandleExtendedHandshakeMessage(PeerId id, ExtendedHandshakeMessage message)
        {
            // FIXME: Use the 'version' information
            // FIXME: Recreate the uri? Give warning?
            if (message.LocalPort > 0)
                id.Peer.LocalPort = message.LocalPort;
            id.MaxSupportedPendingRequests = Math.Max(1, message.MaxRequests);
            id.ExtensionSupports = message.Supports;

            if (id.ExtensionSupports.Supports(PeerExchangeMessage.Support.Name))
            {
                if (_manager.HasMetadata && !_manager.Torrent.IsPrivate)
                    id.PeerExchangeManager = new PeerExchangeManager(id);
            }
        }

        protected virtual void HandleKeepAliveMessage(PeerId id, KeepAliveMessage message)
        {
            id.LastMessageReceived = DateTime.Now;
        }

        protected virtual void HandleNotInterested(PeerId id, NotInterestedMessage message)
        {
            id.IsInterested = false;
        }

        protected virtual void HandlePieceMessage(PeerId id, PieceMessage message)
        {
            id.PiecesReceived++;
            _manager.PieceManager.PieceDataReceived(id, message);

            // Keep adding new piece requests to this peers queue until we reach the max pieces we're allowed queue
            _manager.PieceManager.AddPieceRequests(id);
        }

        protected virtual void HandlePortMessage(PeerId id, PortMessage message)
        {
            id.Port = message.Port;
        }

        protected virtual void HandleRequestMessage(PeerId id, RequestMessage message)
        {
            // If we are not on the last piece and the user requested a stupidly big/small amount of data
            // we will close the connection
            if (_manager.Torrent.Pieces.Count != (message.PieceIndex + 1))
                if (message.RequestLength > RequestMessage.MaxSize || message.RequestLength < RequestMessage.MinSize)
                    throw new MessageException(string.Format("Illegal piece request received. Peer requested {0} byte", message.RequestLength));

            var m = new PieceMessage(message.PieceIndex, message.StartOffset, message.RequestLength);

            // If we're not choking the peer, enqueue the message right away
            if (!id.AmChoking)
            {
                id.IsRequestingPiecesCount++;
                id.PieceReads.Add(m);
                id.TryProcessAsyncReads();
            }

                // If the peer supports fast peer and the requested piece is one of the allowed pieces, enqueue it
                // otherwise send back a reject request message
            else if (id.SupportsFastPeer && ClientEngine.SupportsFastPeer)
            {
                if (id.AmAllowedFastPieces.Contains(message.PieceIndex))
                {
                    id.IsRequestingPiecesCount++;
                    id.PieceReads.Add(m);
                    id.TryProcessAsyncReads();
                }
                else
                    id.Enqueue(new RejectRequestMessage(m));
            }
        }

        protected virtual void HandleHaveMessage(PeerId id, HaveMessage message)
        {
            id.HaveMessagesReceived++;

            // First set the peers bitfield to true for that piece
            id.BitField[message.PieceIndex] = true;

            // Fastcheck to see if a peer is a seeder or not
            id.Peer.IsSeeder = id.BitField.AllTrue;

            // We can do a fast check to see if the peer is interesting or not when we receive a Have Message.
            // If the peer just received a piece we don't have, he's interesting. Otherwise his state is unchanged
            if (!_manager.Bitfield[message.PieceIndex])
                SetAmInterestedStatus(id, true);
        }

        public virtual void HandlePeerConnected(PeerId id, Direction direction)
        {
            var bundle = new MessageBundle();

            AppendBitfieldMessage(id, bundle);
            AppendExtendedHandshake(id, bundle);
            AppendFastPieces(id, bundle);

            id.Enqueue(bundle);
        }

        public virtual void HandlePeerDisconnected(PeerId id)
        {
        }

        protected virtual void AppendExtendedHandshake(PeerId id, MessageBundle bundle)
        {
            if (id.SupportsLTMessages && ClientEngine.SupportsExtended)
                bundle.Messages.Add(
                    new ExtendedHandshakeMessage(_manager.HasMetadata ? _manager.Torrent.Metadata.Length : 0));
        }

        protected virtual void AppendFastPieces(PeerId id, MessageBundle bundle)
        {
            // Now we will enqueue a FastPiece message for each piece we will allow the peer to download
            // even if they are choked
            if (ClientEngine.SupportsFastPeer && id.SupportsFastPeer)
                foreach (var pieceIndex in id.AmAllowedFastPieces)
                    bundle.Messages.Add(new AllowedFastMessage(pieceIndex));
        }

        protected virtual void AppendBitfieldMessage(PeerId id, MessageBundle bundle)
        {
            if (id.SupportsFastPeer && ClientEngine.SupportsFastPeer)
            {
                if (_manager.Bitfield.AllFalse)
                    bundle.Messages.Add(new HaveNoneMessage());

                else if (_manager.Bitfield.AllTrue)
                    bundle.Messages.Add(new HaveAllMessage());

                else
                    bundle.Messages.Add(new BitfieldMessage(_manager.Bitfield));
            }
            else
            {
                bundle.Messages.Add(new BitfieldMessage(_manager.Bitfield));
            }
        }

        public virtual void Tick(int counter)
        {
            PreLogicTick(counter);
            if (_manager.State == TorrentState.Downloading)
                DownloadLogic(counter);
            else if (_manager.State == TorrentState.Seeding)
                SeedingLogic(counter);
            PostLogicTick();
        }

        private void PreLogicTick(int counter)
        {
            //Execute iniitial logic for individual peers
            if (counter%(1000/ClientEngine.TickLength) == 0)
            {
                // Call it every second... ish
                _manager.Monitor.Tick();
                _manager.UpdateLimiters();
            }

            if (_manager.FinishedPieces.Count > 0)
                SendHaveMessagesToAll();

            foreach (var peerId in _manager.Peers.ConnectedPeers)
            {
                if (peerId.Connection == null)
                    continue;

                var maxRequests = PieceManager.NormalRequestAmount +
                                  (int) (peerId.Monitor.DownloadSpeed/1024.0/PieceManager.BonusRequestPerKb);
                maxRequests = Math.Min(peerId.AmRequestingPiecesCount + 2, maxRequests);
                maxRequests = Math.Min(peerId.MaxSupportedPendingRequests, maxRequests);
                maxRequests = Math.Max(2, maxRequests);
                peerId.MaxPendingRequests = maxRequests;

                peerId.Monitor.Tick();
            }
        }

        private void PostLogicTick()
        {
            var nowTime = DateTime.Now;
            var thirtySecondsAgo = nowTime.AddSeconds(-50);
            var nintySecondsAgo = nowTime.AddSeconds(-90);
            var onhundredAndEightySecondsAgo = nowTime.AddSeconds(-180);

            foreach (var id in _manager.Peers.ConnectedPeers.Where(id => id.Connection != null))
            {
                if (id.QueueLength > 0 && !id.ProcessingQueue)
                {
                    id.ProcessingQueue = true;
                    id.ConnectionManager.ProcessQueue(id);
                }

                if (nintySecondsAgo > id.LastMessageSent)
                {
                    id.LastMessageSent = DateTime.Now;
                    id.Enqueue(new KeepAliveMessage());
                }

                if (onhundredAndEightySecondsAgo > id.LastMessageReceived)
                {
                    _manager.Engine.ConnectionManager.CleanupSocket(id, "Inactivity");
                    continue;
                }

                if (thirtySecondsAgo > id.LastMessageReceived && id.AmRequestingPiecesCount > 0)
                    _manager.Engine.ConnectionManager.CleanupSocket(id, "Didn't send pieces");
            }

            var tracker = _manager.TrackerManager.CurrentTracker;
            if (tracker != null && (_manager.State == TorrentState.Seeding || _manager.State == TorrentState.Downloading))
            {
                // If the last connection succeeded, then update at the regular interval
                if (_manager.TrackerManager.UpdateSucceeded)
                {
                    if (DateTime.Now > (_manager.TrackerManager.LastUpdated.Add(tracker.UpdateInterval)))
                    {
                        _manager.TrackerManager.Announce(TorrentEvent.None);
                    }
                }
                    // Otherwise update at the min interval
                else if (DateTime.Now > (_manager.TrackerManager.LastUpdated.Add(tracker.MinUpdateInterval)))
                {
                    _manager.TrackerManager.Announce(TorrentEvent.None);
                }
            }
        }

        private void DownloadLogic(int counter)
        {
            var needAddWebSeeds = (DateTime.Now - _manager.StartTime) > TimeSpan.FromMinutes(1)
                                  && _manager.Monitor.DownloadSpeed < _addWebSeedsSpeedLimit * 1024;
            if (needAddWebSeeds || _addWebSeedsSpeedLimit == 0)
            {
                foreach (var s in _manager.Torrent.GetRightHttpSeeds)
                {
                    var peerId = "-WebSeed-";
                    peerId = peerId + (_webseedCount++).ToString().PadLeft(20 - peerId.Length, '0');

                    var uri = new Uri(s);
                    var peer = new Peer(peerId, uri);
                    var id = new PeerId(peer, _manager);
                    var connection = new HttpConnection(new Uri(s));
                    connection.Manager = _manager;
                    peer.IsSeeder = true;
                    id.BitField.SetAll(true);
                    id.Encryptor = new PlainTextEncryption();
                    id.Decryptor = new PlainTextEncryption();
                    id.IsChoking = false;
                    id.AmInterested = !_manager.Complete;
                    id.Connection = connection;
                    id.ClientApp = new Software(id.PeerID);
                    _manager.Peers.ConnectedPeers.Add(id);
                    _manager.RaisePeerConnected(new PeerConnectionEventArgs(_manager, id, Direction.Outgoing));
                    PeerIO.EnqueueReceiveMessage(
                        id.Connection,
                        id.Decryptor,
                        Manager.DownloadLimiter,
                        id.Monitor,
                        id.TorrentManager,
                        id.ConnectionManager.MessageReceivedCallback,
                        id);
                }

                // FIXME: In future, don't clear out this list. It may be useful to keep the list of HTTP seeds
                // Add a boolean or something so that we don't add them twice.
                _manager.Torrent.GetRightHttpSeeds.Clear();
            }

            // Remove inactive peers we haven't heard from if we're downloading
            if (_manager.State == TorrentState.Downloading
                && _manager.LastCalledInactivePeerManager + TimeSpan.FromSeconds(5) < DateTime.Now)
            {
                _manager.InactivePeerManager.TimePassed();
                _manager.LastCalledInactivePeerManager = DateTime.Now;
            }

            // Now choke/unchoke peers; first instantiate the choke/unchoke manager if we haven't done so already
            if (_manager.ChokeUnchoker == null)
                _manager.ChokeUnchoker = new ChokeUnchokeManager(
                    _manager,
                    _manager.Settings.MinimumTimeBetweenReviews,
                    _manager.Settings.PercentOfMaxRateToSkipReview);
            _manager.ChokeUnchoker.UnchokeReview();
        }

        private void SeedingLogic(int counter)
        {
            //Choke/unchoke peers; first instantiate the choke/unchoke manager if we haven't done so already
            if (_manager.ChokeUnchoker == null)
                _manager.ChokeUnchoker = new ChokeUnchokeManager(_manager, _manager.Settings.MinimumTimeBetweenReviews,
                                                                _manager.Settings.PercentOfMaxRateToSkipReview);

            _manager.ChokeUnchoker.UnchokeReview();
        }

        protected virtual void SetAmInterestedStatus(PeerId id, bool interesting)
        {
            if (interesting && !id.AmInterested)
            {
                id.AmInterested = true;
                id.Enqueue(new InterestedMessage());

                // He's interesting, so attempt to queue up any FastPieces (if that's possible)
                _manager.PieceManager.AddPieceRequests(id);
            }
            else if (!interesting && id.AmInterested)
            {
                id.AmInterested = false;
                id.Enqueue(new NotInterestedMessage());
            }
        }

        private void SendHaveMessagesToAll()
        {
            foreach (var peerId in _manager.Peers.ConnectedPeers)
            {
                if (peerId.Connection == null)
                    continue;

                var bundle = new MessageBundle();

                foreach (var pieceIndex in _manager.FinishedPieces)
                {
                    // If the peer has the piece already, we need to recalculate his "interesting" status.
                    var hasPiece = peerId.BitField[pieceIndex];
                    if (hasPiece)
                    {
                        var isInteresting = _manager.PieceManager.IsInteresting(peerId);
                        SetAmInterestedStatus(peerId, isInteresting);
                    }

                    // Check to see if have supression is enabled and send the have message accordingly
                    if (!hasPiece || (hasPiece && !_manager.Engine.Settings.HaveSupressionEnabled))
                        bundle.Messages.Add(new HaveMessage(pieceIndex));
                }

                peerId.Enqueue(bundle);
            }
            _manager.FinishedPieces.Clear();
        }
    }
}