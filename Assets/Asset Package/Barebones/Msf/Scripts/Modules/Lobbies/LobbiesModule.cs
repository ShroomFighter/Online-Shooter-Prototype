﻿using Barebones.Logging;
using Barebones.Networking;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Barebones.MasterServer
{
    public class LobbiesModule : BaseServerModule, IGamesProvider
    {
        protected Dictionary<string, ILobbyFactory> factories;
        protected Dictionary<int, ILobby> lobbies;
        private int _nextLobbyId;

        [Header("Configuration")]
        public int createLobbiesPermissionLevel = 0;
        [Tooltip("If true, don't allow player to create a lobby if he has already joined one")]
        public bool dontAllowCreatingIfJoined = true;
        [Tooltip("How many lobbies can a user join concurrently")]
        public int joinedLobbiesLimit = 1;

        public SpawnersModule SpawnersModule { get; protected set; }
        public RoomsModule RoomsModule { get; protected set; }

        protected override void Awake()
        {
            base.Awake();

            AddOptionalDependency<SpawnersModule>();
            AddOptionalDependency<RoomsModule>();
        }

        public override void Initialize(IServer server)
        {
            // Get dependencies
            SpawnersModule = server.GetModule<SpawnersModule>();
            RoomsModule = server.GetModule<RoomsModule>();

            factories = factories ?? new Dictionary<string, ILobbyFactory>();
            lobbies = lobbies ?? new Dictionary<int, ILobby>();

            server.SetHandler((short)MsfMessageCodes.CreateLobby, HandleCreateLobby);
            server.SetHandler((short)MsfMessageCodes.JoinLobby, HandleJoinLobby);
            server.SetHandler((short)MsfMessageCodes.LeaveLobby, HandleLeaveLobby);
            server.SetHandler((short)MsfMessageCodes.SetLobbyProperties, HandleSetLobbyProperties);
            server.SetHandler((short)MsfMessageCodes.SetMyLobbyProperties, HandleSetMyProperties);
            server.SetHandler((short)MsfMessageCodes.JoinLobbyTeam, HandleJoinTeam);
            server.SetHandler((short)MsfMessageCodes.LobbySendChatMessage, HandleSendChatMessage);
            server.SetHandler((short)MsfMessageCodes.LobbySetReady, HandleSetReadyStatus);
            server.SetHandler((short)MsfMessageCodes.LobbyStartGame, HandleStartGame);
            server.SetHandler((short)MsfMessageCodes.GetLobbyRoomAccess, HandleGetLobbyRoomAccess);

            server.SetHandler((short)MsfMessageCodes.GetLobbyMemberData, HandleGetLobbyMemberData);
            server.SetHandler((short)MsfMessageCodes.GetLobbyInfo, HandleGetLobbyInfo);
        }

        protected virtual bool CheckIfHasPermissionToCreate(IPeer peer)
        {
            var extension = peer.GetExtension<SecurityInfoPeerExtension>();

            return extension.PermissionLevel >= createLobbiesPermissionLevel;
        }

        public void AddFactory(ILobbyFactory factory)
        {
            // In case the module has not been initialized yet
            if (factories == null)
            {
                factories = new Dictionary<string, ILobbyFactory>();
            }

            if (factories.ContainsKey(factory.Id))
            {
                logger.Warn("You are overriding a factory with same id");
            }

            factories[factory.Id] = factory;
        }

        public bool AddLobby(ILobby lobby)
        {
            if (lobbies.ContainsKey(lobby.Id))
            {
                logger.Error("Failed to add a lobby - lobby with same id already exists");
                return false;
            }

            lobbies.Add(lobby.Id, lobby);

            lobby.OnDestroyedEvent += OnLobbyDestroyed;
            return true;
        }

        /// <summary>
        /// Invoked, when lobby is destroyed
        /// </summary>
        /// <param name="lobby"></param>
        protected virtual void OnLobbyDestroyed(ILobby lobby)
        {
            lobbies.Remove(lobby.Id);
            lobby.OnDestroyedEvent -= OnLobbyDestroyed;
        }

        protected virtual LobbyUserPeerExtension GetOrCreateLobbiesExtension(IPeer peer)
        {
            var extension = peer.GetExtension<LobbyUserPeerExtension>();

            if (extension == null)
            {
                extension = new LobbyUserPeerExtension(peer);
                peer.AddExtension(extension);
            }

            return extension;
        }

        /// <summary>
        /// Create new unique lobby Id
        /// </summary>
        /// <returns></returns>
        public int GenerateLobbyId()
        {
            return _nextLobbyId++;
        }

        #region Message Handlers

        protected virtual void HandleCreateLobby(IIncommingMessage message)
        {
            if (!CheckIfHasPermissionToCreate(message.Peer))
            {
                message.Respond("Insufficient permissions", ResponseStatus.Unauthorized);
                return;
            }

            var lobbiesExt = GetOrCreateLobbiesExtension(message.Peer);

            if (dontAllowCreatingIfJoined && lobbiesExt.CurrentLobby != null)
            {
                // If peer is already in a lobby
                message.Respond("You are already in a lobby", ResponseStatus.Failed);
                return;
            }

            // Deserialize properties of the lobby
            var properties = new Dictionary<string, string>().FromBytes(message.AsBytes());

            if (!properties.ContainsKey(MsfDictKeys.lobbyFactoryId))
            {
                message.Respond("Invalid request (undefined factory)", ResponseStatus.Failed);
                return;
            }

            // Get the lobby factory
            factories.TryGetValue(properties[MsfDictKeys.lobbyFactoryId], out ILobbyFactory factory);

            if (factory == null)
            {
                message.Respond("Unavailable lobby factory", ResponseStatus.Failed);
                return;
            }

            var newLobby = factory.CreateLobby(properties, message.Peer);

            if (!AddLobby(newLobby))
            {
                message.Respond("Lobby registration failed", ResponseStatus.Error);
                return;
            }

            logger.Info("Lobby created: " + newLobby.Id);

            // Respond with success and lobby id
            message.Respond(newLobby.Id, ResponseStatus.Success);
        }

        /// <summary>
        /// Handles a request from user to join a lobby
        /// </summary>
        /// <param name="message"></param>
        protected virtual void HandleJoinLobby(IIncommingMessage message)
        {
            var user = GetOrCreateLobbiesExtension(message.Peer);

            if (user.CurrentLobby != null)
            {
                message.Respond("You're already in a lobby", ResponseStatus.Failed);
                return;
            }

            var lobbyId = message.AsInt();

            ILobby lobby;
            lobbies.TryGetValue(lobbyId, out lobby);

            if (lobby == null)
            {
                message.Respond("Lobby was not found", ResponseStatus.Failed);
                return;
            }

            string error;
            if (!lobby.AddPlayer(user, out error))
            {
                message.Respond(error ?? "Failed to add player to lobby", ResponseStatus.Failed);
                return;
            }

            var data = lobby.GenerateLobbyData(user);

            message.Respond(data, ResponseStatus.Success);
        }

        /// <summary>
        /// Handles a request from user to leave a lobby
        /// </summary>
        /// <param name="message"></param>
        protected virtual void HandleLeaveLobby(IIncommingMessage message)
        {
            var lobbyId = message.AsInt();

            ILobby lobby;
            lobbies.TryGetValue(lobbyId, out lobby);

            var lobbiesExt = GetOrCreateLobbiesExtension(message.Peer);

            if (lobby != null)
            {
                lobby.RemovePlayer(lobbiesExt);
            }

            message.Respond(ResponseStatus.Success);
        }

        protected virtual void HandleSetLobbyProperties(IIncommingMessage message)
        {
            var data = message.Deserialize(new LobbyPropertiesSetPacket());

            ILobby lobby;
            lobbies.TryGetValue(data.LobbyId, out lobby);

            if (lobby == null)
            {
                message.Respond("Lobby was not found", ResponseStatus.Failed);
                return;
            }

            var lobbiesExt = GetOrCreateLobbiesExtension(message.Peer);

            foreach (var dataProperty in data.Properties)
            {
                if (!lobby.SetProperty(lobbiesExt, dataProperty.Key, dataProperty.Value))
                {
                    message.Respond("Failed to set the property: " + dataProperty.Key,
                        ResponseStatus.Failed);
                    return;
                }
            }

            message.Respond(ResponseStatus.Success);
        }

        private void HandleSetMyProperties(IIncommingMessage message)
        {
            var lobbiesExt = GetOrCreateLobbiesExtension(message.Peer);

            var lobby = lobbiesExt.CurrentLobby;

            if (lobby == null)
            {
                message.Respond("Lobby was not found", ResponseStatus.Failed);
                return;
            }

            var properties = new Dictionary<string, string>().FromBytes(message.AsBytes());

            var player = lobby.GetMember(lobbiesExt);

            foreach (var dataProperty in properties)
            {
                // We don't change properties directly,
                // because we want to allow an implementation of lobby
                // to do "sanity" checking
                if (!lobby.SetPlayerProperty(player, dataProperty.Key, dataProperty.Value))
                {
                    message.Respond("Failed to set property: " + dataProperty.Key, ResponseStatus.Failed);
                    return;
                }
            }

            message.Respond(ResponseStatus.Success);
        }

        protected virtual void HandleSetReadyStatus(IIncommingMessage message)
        {
            var isReady = message.AsInt() > 0;

            var lobbiesExt = GetOrCreateLobbiesExtension(message.Peer);
            var lobby = lobbiesExt.CurrentLobby;

            if (lobby == null)
            {
                message.Respond("You're not in a lobby", ResponseStatus.Failed);
                return;
            }

            var member = lobby.GetMember(lobbiesExt);

            if (member == null)
            {
                message.Respond("Invalid request", ResponseStatus.Failed);
                return;
            }

            lobby.SetReadyState(member, isReady);
            message.Respond(ResponseStatus.Success);
        }

        protected virtual void HandleJoinTeam(IIncommingMessage message)
        {
            var data = message.Deserialize(new LobbyJoinTeamPacket());

            var lobbiesExt = GetOrCreateLobbiesExtension(message.Peer);
            var lobby = lobbiesExt.CurrentLobby;

            if (lobby == null)
            {
                message.Respond("You're not in a lobby", ResponseStatus.Failed);
                return;
            }

            var player = lobby.GetMember(lobbiesExt);

            if (player == null)
            {
                message.Respond("Invalid request", ResponseStatus.Failed);
                return;
            }

            if (!lobby.TryJoinTeam(data.TeamName, player))
            {
                message.Respond("Failed to join a team: " + data.TeamName, ResponseStatus.Failed);
                return;
            }

            message.Respond(ResponseStatus.Success);
        }

        protected virtual void HandleSendChatMessage(IIncommingMessage message)
        {
            var lobbiesExt = GetOrCreateLobbiesExtension(message.Peer);
            var lobby = lobbiesExt.CurrentLobby;

            var member = lobby.GetMember(lobbiesExt);

            // Invalid request
            if (member == null)
            {
                return;
            }

            lobby.HandleChatMessage(member, message);
        }

        protected virtual void HandleStartGame(IIncommingMessage message)
        {
            var lobbiesExt = GetOrCreateLobbiesExtension(message.Peer);
            var lobby = lobbiesExt.CurrentLobby;

            if (!lobby.StartGameManually(lobbiesExt))
            {
                message.Respond("Failed starting the game", ResponseStatus.Failed);
                return;
            }

            message.Respond(ResponseStatus.Success);
        }

        protected virtual void HandleGetLobbyRoomAccess(IIncommingMessage message)
        {
            var lobbiesExt = GetOrCreateLobbiesExtension(message.Peer);
            var lobby = lobbiesExt.CurrentLobby;

            lobby.HandleGameAccessRequest(message);
        }

        protected virtual void HandleGetLobbyMemberData(IIncommingMessage message)
        {
            var data = message.Deserialize(new IntPairPacket());
            var lobbyId = data.A;
            var peerId = data.B;

            ILobby lobby;
            lobbies.TryGetValue(lobbyId, out lobby);

            if (lobby == null)
            {
                message.Respond("Lobby not found", ResponseStatus.Failed);
                return;
            }

            var member = lobby.GetMemberByPeerId(peerId);

            if (member == null)
            {
                message.Respond("Player is not in the lobby", ResponseStatus.Failed);
                return;
            }

            message.Respond(member.GenerateDataPacket(), ResponseStatus.Success);
        }

        protected virtual void HandleGetLobbyInfo(IIncommingMessage message)
        {
            var lobbyId = message.AsInt();

            ILobby lobby;
            lobbies.TryGetValue(lobbyId, out lobby);

            if (lobby == null)
            {
                message.Respond("Lobby not found", ResponseStatus.Failed);
                return;
            }

            message.Respond(lobby.GenerateLobbyData(), ResponseStatus.Success);
        }

        #endregion

        public IEnumerable<GameInfoPacket> GetPublicGames(IPeer peer, Dictionary<string, string> filters)
        {
            return lobbies.Values.Select(lobby => new GameInfoPacket()
            {
                Address = lobby.GameIp + ":" + lobby.GamePort,
                Id = lobby.Id,
                IsPasswordProtected = false,
                MaxPlayers = lobby.MaxPlayers,
                Name = lobby.Name,
                OnlinePlayers = lobby.PlayerCount,
                CustomOptions = GetPublicLobbyProperties(peer, lobby, filters),
                Type = GameInfoType.Lobby
            });
        }

        public virtual DictionaryOptions GetPublicLobbyProperties(IPeer peer, ILobby lobby,  Dictionary<string, string> playerFilters)
        {
            return lobby.GetPublicProperties(peer);
        }
    }
}