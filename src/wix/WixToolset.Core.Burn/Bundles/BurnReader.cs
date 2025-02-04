// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.Burn.Bundles
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Xml;
    using WixToolset.Core.Native;
    using WixToolset.Extensibility.Services;

    /// <summary>
    /// Burn PE reader for the WiX toolset.
    /// </summary>
    /// <remarks>This class encapsulates reading from a stub EXE with containers attached
    /// for dissecting bundled/chained setup packages.</remarks>
    /// <example>
    /// using (BurnReader reader = BurnReader.Open(fileExe, this.core, guid))
    /// {
    ///     reader.ExtractUXContainer(file1, tempFolder);
    /// }
    /// </example>
    internal class BurnReader : BurnCommon
    {
        private bool disposed;

        private BinaryReader binaryReader;
        private readonly List<DictionaryEntry> attachedContainerPayloadNames;

        /// <summary>
        /// Creates a BurnReader for reading a PE file.
        /// </summary>
        /// <param name="messaging"></param>
        /// <param name="fileExe">File to read.</param>
        private BurnReader(IMessaging messaging, string fileExe)
            : base(messaging, fileExe)
        {
            this.attachedContainerPayloadNames = new List<DictionaryEntry>();
        }

        /// <summary>
        /// Gets the underlying stream.
        /// </summary>
        public Stream Stream => this.binaryReader?.BaseStream;

        /// <summary>
        /// Opens a Burn reader.
        /// </summary>
        /// <param name="messaging"></param>
        /// <param name="fileExe">Path to file.</param>
        /// <returns>Burn reader.</returns>
        public static BurnReader Open(IMessaging messaging, string fileExe)
        {
            var binaryReader = new BinaryReader(File.Open(fileExe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
            var reader = new BurnReader(messaging, fileExe)
            {
                binaryReader = binaryReader,
            };
            reader.Initialize(reader.binaryReader);

            return reader;
        }

        /// <summary>
        /// Gets the UX container from the exe and extracts its contents to the output directory.
        /// </summary>
        /// <param name="outputDirectory">Directory to write extracted files to.</param>
        /// <param name="tempDirectory">Scratch directory.</param>
        /// <returns>True if successful, false otherwise</returns>
        public bool ExtractUXContainer(string outputDirectory, string tempDirectory)
        {
            // No UX container to extract
            if (this.AttachedContainers.Count == 0)
            {
                return false;
            }

            if (this.Invalid)
            {
                return false;
            }

            Directory.CreateDirectory(outputDirectory);
            var tempCabPath = Path.Combine(tempDirectory, "ux.cab");
            var manifestOriginalPath = Path.Combine(outputDirectory, "0");
            var manifestPath = Path.Combine(outputDirectory, "manifest.xml");
            var uxContainerSlot = this.AttachedContainers[0];

            this.binaryReader.BaseStream.Seek(this.UXAddress, SeekOrigin.Begin);
            using (Stream tempCab = File.Open(tempCabPath, FileMode.Create, FileAccess.Write))
            {
                BurnCommon.CopyStream(this.binaryReader.BaseStream, tempCab, (int)uxContainerSlot.Size);
            }

            var cabinet = new Cabinet(tempCabPath);
            cabinet.Extract(outputDirectory);

            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
            FileSystem.MoveFile(manifestOriginalPath, manifestPath);

            var document = new XmlDocument();
            document.Load(manifestPath);
            var namespaceManager = new XmlNamespaceManager(document.NameTable);
            namespaceManager.AddNamespace("burn", document.DocumentElement.NamespaceURI);
            var uxPayloads = document.SelectNodes("/burn:BurnManifest/burn:UX/burn:Payload", namespaceManager);
            var payloads = document.SelectNodes("/burn:BurnManifest/burn:Payload", namespaceManager);

            foreach (XmlNode uxPayload in uxPayloads)
            {
                var sourcePathNode = uxPayload.Attributes.GetNamedItem("SourcePath");
                var filePathNode = uxPayload.Attributes.GetNamedItem("FilePath");

                var sourcePath = Path.Combine(outputDirectory, sourcePathNode.Value);
                var destinationPath = Path.Combine(outputDirectory, filePathNode.Value);

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                FileSystem.MoveFile(sourcePath, destinationPath);
            }

            foreach (XmlNode payload in payloads)
            {
                var packagingNode = payload.Attributes.GetNamedItem("Packaging");

                var packaging = packagingNode.Value;

                if (packaging.Equals("embedded", StringComparison.OrdinalIgnoreCase))
                {
                    var sourcePathNode = payload.Attributes.GetNamedItem("SourcePath");
                    var filePathNode = payload.Attributes.GetNamedItem("FilePath");
                    var containerNode = payload.Attributes.GetNamedItem("Container");

                    var sourcePath = sourcePathNode.Value;
                    var destinationPath = Path.Combine(containerNode.Value, filePathNode.Value);

                    this.attachedContainerPayloadNames.Add(new DictionaryEntry(sourcePath, destinationPath));
                }
            }

            return true;
        }

        /// <summary>
        /// Gets each non-UX attached container from the exe and extracts its contents to the output directory.
        /// </summary>
        /// <param name="outputDirectory">Directory to write extracted files to.</param>
        /// <param name="tempDirectory">Scratch directory.</param>
        /// <returns>True if successful, false otherwise</returns>
        public bool ExtractAttachedContainers(string outputDirectory, string tempDirectory)
        {
            // No attached containers to extract
            if (this.AttachedContainers.Count < 2)
            {
                return false;
            }

            if (this.Invalid)
            {
                return false;
            }

            Directory.CreateDirectory(outputDirectory);
            var nextAddress = this.EngineSize;
            for (var i = 1; i < this.AttachedContainers.Count; i++)
            {
                var cntnr = this.AttachedContainers[i];
                var tempCabPath = Path.Combine(tempDirectory, $"a{i}.cab");

                this.binaryReader.BaseStream.Seek(nextAddress, SeekOrigin.Begin);
                using (Stream tempCab = File.Open(tempCabPath, FileMode.Create, FileAccess.Write))
                {
                    BurnCommon.CopyStream(this.binaryReader.BaseStream, tempCab, (int)cntnr.Size);
                }

                var cabinet = new Cabinet(tempCabPath);
                cabinet.Extract(outputDirectory);

                nextAddress += cntnr.Size;
            }

            foreach (var entry in this.attachedContainerPayloadNames)
            {
                var sourcePath = Path.Combine(outputDirectory, (string)entry.Key);
                var destinationPath = Path.Combine(outputDirectory, (string)entry.Value);

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                FileSystem.MoveFile(sourcePath, destinationPath);
            }

            return true;
        }

        /// <summary>
        /// Dispose object.
        /// </summary>
        /// <param name="disposing">True when releasing managed objects.</param>
        protected override void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                if (disposing && this.binaryReader != null)
                {
                    this.binaryReader.Close();
                    this.binaryReader = null;
                }

                this.disposed = true;
            }
        }
    }
}
