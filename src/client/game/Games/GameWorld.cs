﻿/*
 * Copyright (C) 2011 voxeliq project 
 *
 */

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VolumetricStudios.VoxeliqClient.Graphics.Builders;
using VolumetricStudios.VoxeliqEngine.Common.Logging;
using VolumetricStudios.VoxeliqEngine.Profiling;
using VolumetricStudios.VoxeliqEngine.Screen;
using VolumetricStudios.VoxeliqEngine.Universe;
using VolumetricStudios.VoxeliqEngine.Utils.Vector;

namespace VolumetricStudios.VoxeliqClient.Games
{
    /// <summary>
    /// The game world.
    /// </summary>
    public class GameWorld : World, IGameComponent, IDrawable, IWorldStatisticsService
    {
        /// <summary>
        /// fog vectors.
        /// </summary>
        private readonly Vector2[] _fogVectors = new[] { new Vector2(0, 0), new Vector2(175, 250), new Vector2(250, 400) };

        /// <summary>
        /// Fog state.
        /// </summary>
        public FogState FogState { get; private set; }

        /// <summary>
        /// camera controller
        /// </summary>
        private ICameraControlService _cameraController;

        public bool Visible
        {
            get { return true; }
        }

        public int DrawOrder { get; set; }

        public event EventHandler<EventArgs> VisibleChanged;
        public event EventHandler<EventArgs> DrawOrderChanged;

        /// <summary>
        /// The camera service.
        /// </summary>
        public ICameraService Camera;

        /// <summary>
        /// Chunk builder.
        /// </summary>
        public ChunkBuilder ChunkBuilder { get; protected set; }

        /// <summary>
        /// Generation queue count.
        /// </summary>
        public int GenerationQueueCount { get { return this.ChunkBuilder.GenerationQueueCount; } }

        /// <summary>
        /// Building queue count.
        /// </summary>
        public int BuildingQueueCount { get { return this.ChunkBuilder.BuildingQueueCount; } }

        public Game Game { get; private set; }

        /// <summary>
        /// player
        /// </summary>
        private IPlayer _player;

        /// <summary>
        /// block effect.
        /// </summary>
        private Effect _blockEffect;

        /// <summary>
        /// block texture atlas
        /// </summary>
        private Texture2D _blockTextureAtlas;

        /// <summary>
        /// crack texture atlas
        /// </summary>
        private Texture2D _crackTextureAtlas;

        public int UpdateOrder { get; set; }

        /// <summary>
        /// Logging facility.
        /// </summary>
        private static readonly Logger Logger = LogManager.CreateLogger();

        public GameWorld(Game game):
            base(true) // set world to infinitive.
        {
            this.Game = game;
            this.Game.Services.AddService(typeof(IWorldStatisticsService), this);
            this.Game.Services.AddService(typeof(IWorldService), this);
        }

        public void Initialize()
        {
            Logger.Trace("init()");

            this.FogState = FogState.None; // fog-state.

            // TODO: these are client stuff.
            this.Camera = (ICameraService)this.Game.Services.GetService(typeof(ICameraService)); //
            this._cameraController = (ICameraControlService)this.Game.Services.GetService(typeof(ICameraControlService));
            this._player = (IPlayer)this.Game.Services.GetService(typeof(IPlayer));

            this.Chunks = new ChunkManager(); // startup the chunk manager.
            this.ChunkBuilder = new QueuedBuilder(this._player, this); // the chunk builder.        

            this._blockEffect = Game.Content.Load<Effect>("Effects\\BlockEffect");
            this._blockTextureAtlas = Game.Content.Load<Texture2D>("Textures\\blocks");
            this._crackTextureAtlas = Game.Content.Load<Texture2D>("Textures\\cracks");

            this._cameraController.LookAt(Vector3.Down);
            this._player.SpawnPlayer(new Vector2Int(1000, 1000)); // TODO: client stuff.
        }

        // TODO: client stuff.
        public void ToggleFog()
        {
            switch (FogState)
            {
                case FogState.None:
                    FogState = FogState.Near;
                    break;
                case FogState.Near:
                    FogState = FogState.Far;
                    break;
                case FogState.Far:
                    FogState = FogState.None;
                    break;
            }
        }

        // TODO: client stuff.
        public void SpawnPlayer(Vector2Int relativePosition)
        {
            Profiler.Start("terrain-generation");
            for (int z = -ViewRange; z <= ViewRange; z++)
            {
                for (int x = -ViewRange; x <= ViewRange; x++)
                {
                    var chunk = new Chunk(this, new Vector2Int(relativePosition.X + x, relativePosition.Z + z));
                    this.Chunks[chunk.RelativePosition.X, chunk.RelativePosition.Z] = chunk;

                    if (chunk.RelativePosition == relativePosition) this._player.CurrentChunk = chunk;
                }
            }

            this.Chunks.SouthWestEdge = new Vector2Int(relativePosition.X - ViewRange, relativePosition.Z - ViewRange);
            this.Chunks.NorthEastEdge = new Vector2Int(relativePosition.X + ViewRange, relativePosition.Z + ViewRange);

            BoundingBox = new BoundingBox(new Vector3(this.Chunks.SouthWestEdge.X * Chunk.WidthInBlocks, 0, this.Chunks.SouthWestEdge.Z * Chunk.LenghtInBlocks), new Vector3((this.Chunks.NorthEastEdge.X + 1) * Chunk.WidthInBlocks, Chunk.HeightInBlocks, (this.Chunks.NorthEastEdge.Z + 1) * Chunk.LenghtInBlocks));

            this.ChunkBuilder.Start();
        }

        #region world-drawer

        public void Draw(GameTime gameTime)
        {
            var viewFrustrum = new BoundingFrustum(this.Camera.View * this.Camera.Projection);

            Game.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
            Game.GraphicsDevice.BlendState = BlendState.Opaque;

            _blockEffect.Parameters["World"].SetValue(Matrix.Identity);
            _blockEffect.Parameters["View"].SetValue(this.Camera.View);
            _blockEffect.Parameters["Projection"].SetValue(this.Camera.Projection);
            _blockEffect.Parameters["CameraPosition"].SetValue(this.Camera.Position);
            _blockEffect.Parameters["FogColor"].SetValue(Color.White.ToVector4());
            _blockEffect.Parameters["FogNear"].SetValue(this._fogVectors[(byte)this.FogState].X);
            _blockEffect.Parameters["FogFar"].SetValue(this._fogVectors[(byte)this.FogState].Y);
            _blockEffect.Parameters["SunColor"].SetValue(Color.White.ToVector3());
            _blockEffect.Parameters["BlockTextureAtlas"].SetValue(_blockTextureAtlas);

            this.ChunksDrawn = 0;
            foreach (EffectPass pass in this._blockEffect.CurrentTechnique.Passes)
            {
                pass.Apply();

                foreach (Chunk chunk in this.Chunks.Values)
                {
                    if (!chunk.Generated || !chunk.BoundingBox.Intersects(viewFrustrum) || chunk.IndexBuffer == null) continue;

                    lock (chunk)
                    {
                        if (chunk.IndexBuffer.IndexCount == 0) continue;
                        Game.GraphicsDevice.SetVertexBuffer(chunk.VertexBuffer);
                        Game.GraphicsDevice.Indices = chunk.IndexBuffer;
                        Game.GraphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, chunk.VertexBuffer.VertexCount, 0, chunk.IndexBuffer.IndexCount / 3);
                    }

                    this.ChunksDrawn++;
                }
            }
        }

        #endregion
    }

    // TODO: client stuff.
    public enum FogState : byte
    {
        None,
        Near,
        Far
    }

    /// <summary>
    /// Statistics interface.
    /// </summary>
    public interface IWorldStatisticsService
    {
        int TotalChunks { get; }
        int ChunksDrawn { get; }
        int GenerationQueueCount { get; }
        int BuildingQueueCount { get; }
        bool IsInfinitive { get; }
        FogState FogState { get; }
    }
}