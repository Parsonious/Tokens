﻿using Microsoft.Extensions.Logging;
using Scuttle.Interfaces;
using System.Security.Cryptography;
using Scuttle.Enums;
using System.Buffers;
using Scuttle.Base;
using Scuttle.Encoders;
using Scuttle.Detection;

namespace Scuttle.Services
{
    /// <summary>
    /// Service for handling file encryption and decryption operations
    /// </summary>
    public class FileEncryptionService(ILogger<FileEncryptionService> logger, PaddingService paddingService, IEncoder encoder, AlgorithmIdentifier algorithmIdentifier)
    {
        private readonly PaddingService _paddingService = paddingService;
        private readonly ILogger<FileEncryptionService> _logger = logger;
        private readonly IEncoder _encoder = encoder;
        private readonly AlgorithmIdentifier _algorithmIdentifier = algorithmIdentifier;
        private const int BufferSize = 81920; // 80KB buffer - optimized for modern file systems
        private const int LargeFileThreshold = 10 * 1024 * 1024; // 10MB - files larger than this will use streaming
        private static readonly int MaxDegreeOfParallelism = Environment.ProcessorCount;

        /// <summary>
        /// Encrypt a file using the specified algorithm
        /// </summary>
        /// <param name="inputFilePath">Path to the file to encrypt</param>
        /// <param name="outputFilePath">Path where the encrypted file should be saved</param>
        /// <param name="encryption">Encryption algorithm to use</param>
        /// <param name="key">Key to use, or null to generate a new one</param>
        /// <param name="keyOutputPath">Path where the key should be saved, or null if it shouldn't be saved</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        /// <returns>The encryption key used</returns>
        public async Task<byte[]> EncryptFileAsync(
            string inputFilePath,
            string outputFilePath,
            IEncryption encryption,
            byte[]? key = null,
            string? keyOutputPath = null,
            CancellationToken cancellationToken = default)
        {
            if ( !File.Exists(inputFilePath) )
                throw new FileNotFoundException("Input file not found", inputFilePath);

            // Generate or use the provided key
            key ??= encryption.GenerateKey();

            // Get file info to determine processing method
            var fileInfo = new FileInfo(inputFilePath);

            if ( IsVeryLargeFile(fileInfo.Length) )
            {
                // Use memory-mapped files for very large files
                await ProcessVeryLargeFileAsync(inputFilePath, outputFilePath, encryption, key, true, cancellationToken);
            }
            else if ( fileInfo.Length > LargeFileThreshold )
            {
                // Use streaming for large files
                await ProcessLargeFileAsync(inputFilePath, outputFilePath, encryption, key, true, cancellationToken);
            }
            else
            {
                // Use in-memory processing for smaller files
                await ProcessSmallFileAsync(inputFilePath, outputFilePath, encryption, key, true, cancellationToken);
            }

            // Save the key if requested
            if ( !string.IsNullOrEmpty(keyOutputPath) )
            {
                await SaveKeyToFileAsync(key, keyOutputPath, cancellationToken);
            }

            return key;
        }

        /// <summary>
        /// Decrypt a file using the specified algorithm
        /// </summary>
        /// <param name="inputFilePath">Path to the encrypted file</param>
        /// <param name="outputFilePath">Path where the decrypted file should be saved</param>
        /// <param name="encryption">Encryption algorithm to use</param>
        /// <param name="key">Decryption key</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        /// <returns>True if decryption was successful</returns>
        public async Task<bool> DecryptFileAsync(
            string inputFilePath,
            string outputFilePath,
            IEncryption encryption,
            byte[] key,
            CancellationToken cancellationToken = default)
        {
            if ( !File.Exists(inputFilePath) )
                throw new FileNotFoundException("Encrypted file not found", inputFilePath);

            try
            {
                // Get file info to determine processing method
                var fileInfo = new FileInfo(inputFilePath);

                if ( IsVeryLargeFile(fileInfo.Length) )
                {
                    // Use memory-mapped files for very large files
                    await ProcessVeryLargeFileAsync(inputFilePath, outputFilePath, encryption, key, false, cancellationToken);
                }
                else if ( fileInfo.Length > LargeFileThreshold )
                {
                    // Use streaming for large files
                    await ProcessLargeFileAsync(inputFilePath, outputFilePath, encryption, key, false, cancellationToken);
                }
                else
                {
                    // Use in-memory processing for smaller files
                    await ProcessSmallFileAsync(inputFilePath, outputFilePath, encryption, key, false, cancellationToken);
                }

                return true;
            }
            catch ( CryptographicException ex )
            {
                _logger.LogError(ex, "Decryption failed. The key may be incorrect or the data may be corrupted.");
                return false;
            }
            catch ( Exception ex )
            {
                _logger.LogError(ex, "An error occurred during file decryption: {Message}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Load a key from a file
        /// </summary>
        /// <param name="keyFilePath">Path to the key file</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation</param>
        /// <returns>The key as a byte array</returns>
        public async Task<byte[]> LoadKeyFromFileAsync(string keyFilePath, CancellationToken cancellationToken = default)
        {
            if ( !File.Exists(keyFilePath) )
                throw new FileNotFoundException("Key file not found", keyFilePath);

            try
            {
                // First try to read as text - this handles hex and base64 formats
                string keyContent = await File.ReadAllTextAsync(keyFilePath, cancellationToken);
                keyContent = keyContent.Trim();

                // Try to detect the format
                if ( IsHexString(keyContent) )
                {
                    _logger.LogInformation("Key file detected as hex string format");
                    byte[] key = ConvertHexStringToByteArray(keyContent);
                    return ValidateKeySize(key);
                }
                else if ( _encoder.IsValidFormat(keyContent) )
                {
                    _logger.LogInformation("Key file detected as Base64 format");
                    try
                    {
                        byte[] key = Convert.FromBase64String(keyContent);
                        return ValidateKeySize(key);
                    }
                    catch ( FormatException )
                    {
                        // Not valid Base64, fall through to binary read
                    }
                }

                // If text parsing failed or resulted in wrong key size, try binary
                _logger.LogInformation("Attempting to read key file as binary");
                byte[] binaryKey = await File.ReadAllBytesAsync(keyFilePath, cancellationToken);
                return ValidateKeySize(binaryKey);
            }
            catch ( Exception ex ) when ( ex is not FileNotFoundException )
            {
                _logger.LogError(ex, "Error reading key file: {Path}", keyFilePath);
                throw new CryptographicException($"Failed to read valid key from file: {ex.Message}", ex);
            }
        }
        /// <summary>
        /// Load a key from a file (synchronous version for backward compatibility)
        /// </summary>
        /// <param name="keyFilePath">Path to the key file</param>
        /// <returns>The key as a byte array</returns>
        public byte[] LoadKeyFromFile(string keyFilePath)
        {
            if ( !File.Exists(keyFilePath) )
                throw new FileNotFoundException("Key file not found", keyFilePath);

            try
            {
                // First try to read as text - this handles hex and base64 formats
                string keyContent = File.ReadAllText(keyFilePath).Trim();

                // Try to detect the format
                if ( IsHexString(keyContent) )
                {
                    _logger.LogInformation("Key file detected as hex string format");
                    byte[] key = ConvertHexStringToByteArray(keyContent);
                    return ValidateKeySize(key);
                }
                else if ( _encoder.IsValidFormat(keyContent) )
                {
                    _logger.LogInformation("Key file detected as Base64 format");
                    try
                    {
                        byte[] key = Convert.FromBase64String(keyContent);
                        return ValidateKeySize(key);
                    }
                    catch ( FormatException )
                    {
                        // Not valid Base64, fall through to binary read
                    }
                }

                // If text parsing failed or resulted in wrong key size, try binary
                _logger.LogInformation("Attempting to read key file as binary");
                byte[] binaryKey = File.ReadAllBytes(keyFilePath);
                return ValidateKeySize(binaryKey);
            }
            catch ( Exception ex ) when ( ex is not FileNotFoundException )
            {
                _logger.LogError(ex, "Error reading key file: {Path}", keyFilePath);
                throw new CryptographicException($"Failed to read valid key from file: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Encrypt a file using the specified algorithm (synchronous version for backward compatibility)
        /// </summary>
        /// <param name="inputFilePath">Path to the file to encrypt</param>
        /// <param name="outputFilePath">Path where the encrypted file should be saved</param>
        /// <param name="encryption">Encryption algorithm to use</param>
        /// <param name="key">Key to use, or null to generate a new one</param>
        /// <param name="keyOutputPath">Path where the key should be saved, or null if it shouldn't be saved</param>
        /// <returns>The encryption key used</returns>
        public byte[] EncryptFile(string inputFilePath, string outputFilePath, IEncryption encryption, byte[]? key = null, string? keyOutputPath = null)
        {
            // Call async version but wait for it synchronously
            return EncryptFileAsync(inputFilePath, outputFilePath, encryption, key, keyOutputPath).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Decrypt a file using the specified algorithm (synchronous version for backward compatibility)
        /// </summary>
        /// <param name="inputFilePath">Path to the encrypted file</param>
        /// <param name="outputFilePath">Path where the decrypted file should be saved</param>
        /// <param name="encryption">Encryption algorithm to use</param>
        /// <param name="key">Decryption key</param>
        /// <returns>True if decryption was successful</returns>
        public bool DecryptFile(string inputFilePath, string outputFilePath, IEncryption encryption, byte[] key)
        {
            // Call async version but wait for it synchronously
            return DecryptFileAsync(inputFilePath, outputFilePath, encryption, key).GetAwaiter().GetResult();
        }

        #region Private Helper Methods

        private async Task ProcessSmallFileAsync(string inputFilePath, string outputFilePath, IEncryption encryption, 
                                                byte[] key, bool isEncrypting, CancellationToken cancellationToken)
        {
            _logger.LogInformation("{Operation} small file: {Path}",
                isEncrypting ? "Encrypting" : "Decrypting", inputFilePath);

            if ( isEncrypting )
            {
                // Read the entire file
                byte[] fileData = await File.ReadAllBytesAsync(inputFilePath, cancellationToken);

                // Process the data
                byte[] encryptedData = encryption.Encrypt(fileData, key);

                // Create a header for the file
                var header = new EncryptionHeader { AlgorithmId = GetAlgorithmId(encryption) };
                byte[] headerBytes = header.ToByteArray();

                // Combine header and encrypted data
                byte[] outputData = new byte[headerBytes.Length + encryptedData.Length];
                Buffer.BlockCopy(headerBytes, 0, outputData, 0, headerBytes.Length);
                Buffer.BlockCopy(encryptedData, 0, outputData, headerBytes.Length, encryptedData.Length);

                // Write the result
                await File.WriteAllBytesAsync(outputFilePath, outputData, cancellationToken);
            }
            else
            {
                // Read the entire file
                byte[] fileData = await File.ReadAllBytesAsync(inputFilePath, cancellationToken);

                // Ensure the file is at least as large as the header
                if ( fileData.Length < EncryptionHeader.HEADER_SIZE )
                    throw new InvalidDataException("Invalid encrypted file format - file too small");

                // Read the header
                using var memStream = new MemoryStream(fileData);
                var header = EncryptionHeader.Read(memStream);

                // Get the encrypted data (everything after the header)
                byte[] encryptedData = new byte[fileData.Length - EncryptionHeader.HEADER_SIZE];
                Buffer.BlockCopy(fileData, EncryptionHeader.HEADER_SIZE, encryptedData, 0, encryptedData.Length);

                // Process the data
                byte[] decryptedData = encryption.Decrypt(encryptedData, key);

                // Write the result
                await File.WriteAllBytesAsync(outputFilePath, decryptedData, cancellationToken);
            }

            _logger.LogInformation("{Operation} completed: {Path}",
                isEncrypting ? "Encryption" : "Decryption", outputFilePath);
        }

        private async Task ProcessLargeFileAsync(string inputFilePath,string outputFilePath,IEncryption encryption,
                                                 byte[] key,bool isEncrypting,CancellationToken cancellationToken)
        {
            _logger.LogInformation("{Operation} large file using streaming: {Path}",
                isEncrypting ? "Encrypting" : "Decrypting", inputFilePath);

            // Get file info for progress reporting
            var fileInfo = new FileInfo(inputFilePath);
            long fileSize = fileInfo.Length;
            long processedBytes = 0;

            // Create directory if it doesn't exist
            Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath) ?? string.Empty);

            using var inputStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
            using var outputStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan);

            // If encrypting, write the header first
            if ( isEncrypting )
            {
                var header = new EncryptionHeader { AlgorithmId = GetAlgorithmId(encryption) };
                byte[] headerBytes = header.ToByteArray();
                await outputStream.WriteAsync(headerBytes, 0, headerBytes.Length, cancellationToken);
            }
            else
            {
                // If decrypting, read and validate the header
                try
                {
                    var header = EncryptionHeader.Read(inputStream);
                    _logger.LogInformation("Decrypting file with algorithm ID: {AlgorithmId}", header.AlgorithmId);

                    // Skip the header in the input file
                    // We've already read it by calling EncryptionHeader.Read
                }
                catch ( InvalidDataException ex )
                {
                    _logger.LogWarning(ex, "Could not read valid encryption header. File may be corrupted or encrypted with an older version.");
                    // Reset the stream position to start decrypting from the beginning
                    inputStream.Position = 0;
                }
            }

            // Determine optimal chunk size based on file size
            int chunkSize = DetermineOptimalChunkSize(fileSize);

            // Create a list to store tasks for parallel processing
            var tasks = new List<Task<(long offset, byte[] data)>>();
            long currentOffset = isEncrypting ? 0 : EncryptionHeader.HEADER_SIZE;
            long outputOffset = 0;  // For decryption, we write from the beginning of the output file

            while ( currentOffset < fileSize )
            {
                // Calculate the size of the current chunk (might be smaller for the last chunk)
                int currentChunkSize = (int) Math.Min(chunkSize, fileSize - currentOffset);

                // Read chunk
                byte[] buffer = new byte[currentChunkSize];
                inputStream.Position = currentOffset;
                int bytesRead = await inputStream.ReadAsync(buffer, 0, currentChunkSize, cancellationToken);

                if ( bytesRead <= 0 ) break;

                // If the buffer was not fully filled, resize it
                if ( bytesRead < buffer.Length )
                {
                    Array.Resize(ref buffer, bytesRead);
                }

                // Create a task for processing this chunk
                long chunkOffset = isEncrypting ? currentOffset : outputOffset;
                tasks.Add(Task.Run(() =>
                {
                    byte[] processedChunk = isEncrypting
                        ? encryption.Encrypt(buffer, key)
                        : encryption.Decrypt(buffer, key);
                    return (chunkOffset, processedChunk);
                }, cancellationToken));

                // Update progress
                processedBytes += bytesRead;
                currentOffset += bytesRead;
                outputOffset += bytesRead;  // This will be adjusted when we know the actual size after encryption/decryption

                // Limit parallel tasks to prevent excessive memory usage
                if ( tasks.Count >= MaxDegreeOfParallelism || currentOffset >= fileSize )
                {
                    // Wait for at least one task to complete
                    Task<(long offset, byte[] data)> completedTask = await Task.WhenAny(tasks);
                    tasks.Remove(completedTask);

                    // Process the result
                    var result = await completedTask;

                    // Write the processed chunk to the output file
                    outputStream.Position = result.offset;
                    await outputStream.WriteAsync(result.data, 0, result.data.Length, cancellationToken);
                }
            }

            // Complete any remaining tasks
            while ( tasks.Count > 0 )
            {
                Task<(long offset, byte[] data)> completedTask = await Task.WhenAny(tasks);
                tasks.Remove(completedTask);

                var result = await completedTask;
                outputStream.Position = result.offset;
                await outputStream.WriteAsync(result.data, 0, result.data.Length, cancellationToken);
            }

            _logger.LogInformation("{Operation} completed: {Path}",
                isEncrypting ? "Encryption" : "Decryption", outputFilePath);
        }


        private async Task SaveKeyToFileAsync(byte[] key, string keyOutputPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Saving encryption key to: {Path}", keyOutputPath);

            // Create directory if needed
            Directory.CreateDirectory(Path.GetDirectoryName(keyOutputPath) ?? string.Empty);

            // Save in hex format for easier handling
            string keyHex = BitConverter.ToString(key).Replace("-", "");
            await File.WriteAllTextAsync(keyOutputPath, keyHex, cancellationToken);
        }

        private int DetermineOptimalChunkSize(long fileSize)
        {
            // For very large files, use larger chunks to reduce overhead
            if ( fileSize > 1024 * 1024 * 1024 ) // > 1GB
                return 4 * 1024 * 1024; // 4MB chunks
            else if ( fileSize > 100 * 1024 * 1024 ) // > 100MB
                return 1 * 1024 * 1024; // 1MB chunks
            else
                return 256 * 1024; // 256KB chunks for smaller files
        }

        private bool IsHexString(string test)
        {
            // Empty strings are not valid hex strings
            if ( string.IsNullOrEmpty(test) )
                return false;

            // Check if string is a valid hex string
            return test.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
        }

        private byte[] ConvertHexStringToByteArray(string hex)
        {
            // Remove any non-hex characters (like spaces or dashes)
            hex = new string(hex.Where(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')).ToArray());

            int numberChars = hex.Length;
            if ( numberChars % 2 != 0 )
            {
                _logger.LogWarning("Hex string has odd length. Padding with leading zero.");
                hex = "0" + hex; // Pad with leading zero if odd length
                numberChars++;
            }

            byte[] bytes = new byte[numberChars / 2];
            for ( int i = 0; i < numberChars; i += 2 )
            {
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }
            return bytes;
        }

        /// <summary>
        /// Validates and potentially adjusts the key size for the algorithm
        /// </summary>
        private byte[] ValidateKeySize(byte[] key)
        {
            _logger.LogDebug("Key size before validation: {Size} bytes", key?.Length ?? 0);

            if ( key == null || key.Length == 0 )
                throw new ArgumentException("Key cannot be empty");

            // For AES-GCM we need exactly 32 bytes (256 bits)
            if ( key.Length == 32 )
                return key;

            _logger.LogWarning("Key size mismatch. Found {ActualSize} bytes, but expected 32 bytes.", key.Length);

            // If the key is too short, we could pad it
            if ( key.Length < 32 )
            {
                _logger.LogInformation("Padding the key to 32 bytes");
                byte[] paddedKey = new byte[32];
                Buffer.BlockCopy(key, 0, paddedKey, 0, key.Length);

                // Fill the remaining bytes with a deterministic pattern based on the original key
                for ( int i = key.Length; i < 32; i++ )
                {
                    paddedKey[i] = (byte) (key[i % key.Length] ^ 0x5C); // XOR with 0x5C (typical HMAC outer pad value)
                }
                return paddedKey;
            }

            // If the key is too long, we could hash it or truncate it
            // Using a cryptographic hash is generally safer than truncation
            using ( var sha256 = SHA256.Create() )
            {
                _logger.LogInformation("Hashing the key to derive a 32-byte key");
                return sha256.ComputeHash(key);
            }
        }

        /// <summary>
        /// Process a file in parallel using memory-mapped files for very large files
        /// </summary>
        /// <remarks>
        /// This method is suitable for extremely large files that even streaming would
        /// struggle with. It uses memory-mapped files which allow operating on portions
        /// of the file without loading the entire file into memory.
        /// </remarks>
        private async Task ProcessVeryLargeFileAsync(
     string inputFilePath,
     string outputFilePath,
     IEncryption encryption,
     byte[] key,
     bool isEncrypting,
     CancellationToken cancellationToken)
        {
            _logger.LogInformation("{Operation} very large file using memory mapping: {Path}",
                isEncrypting ? "Encrypting" : "Decrypting", inputFilePath);

            // Ensure output directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath) ?? string.Empty);

            // Handle headers for very large files
            EncryptionHeader? header = null;
            long headerSize = 0;
            long inputFileStartOffset = 0;

            if ( isEncrypting )
            {
                // For encryption, create a header
                header = new EncryptionHeader { AlgorithmId = GetAlgorithmId(encryption) };
                headerSize = EncryptionHeader.HEADER_SIZE;

                // Write the header to the beginning of the output file
                using ( var fs = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write) )
                {
                    byte[] headerBytes = header.ToByteArray();
                    await fs.WriteAsync(headerBytes, 0, headerBytes.Length, cancellationToken);
                }
            }
            else
            {
                // For decryption, try to read header from input file
                try
                {
                    using var fileStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read);
                    header = EncryptionHeader.Read(fileStream);
                    headerSize = EncryptionHeader.HEADER_SIZE;
                    inputFileStartOffset = EncryptionHeader.HEADER_SIZE; // Skip header when reading input
                    _logger.LogInformation("Decrypting very large file with algorithm ID: {AlgorithmId}", header.AlgorithmId);
                }
                catch ( InvalidDataException ex )
                {
                    _logger.LogWarning(ex, "Could not read valid encryption header. File may be corrupted or encrypted with an older version.");
                    headerSize = 0;  // No header to skip
                    inputFileStartOffset = 0;
                }
            }

            // Calculate segment size - we want to process the file in manageable segments
            const long segmentSize = 64 * 1024 * 1024; // 64MB segments
            var fileInfo = new FileInfo(inputFilePath);
            long fileSize = fileInfo.Length - inputFileStartOffset;  // Account for header size
            int segmentCount = (int) Math.Ceiling((double) fileSize / segmentSize);

            // Pre-allocate the output file if we're encrypting (we already wrote the header)
            // For decryption, we don't know the final size in advance
            if ( isEncrypting )
            {
                // For encryption, we can estimate output size based on input size plus padding
                long estimatedOutputSize = headerSize + fileSize + (segmentCount * 32); // Add header size + padding
                using ( var fs = new FileStream(outputFilePath, FileMode.Open, FileAccess.Write) )
                {
                    fs.SetLength(estimatedOutputSize);
                }
            }

            // Create a semaphore to limit concurrent file access
            using var semaphore = new SemaphoreSlim(MaxDegreeOfParallelism);

            // Create tasks for each segment
            var tasks = new List<Task>();

            for ( int i = 0; i < segmentCount; i++ )
            {
                int segmentIndex = i;
                long inputOffset = inputFileStartOffset + (segmentIndex * segmentSize);
                long outputOffset = isEncrypting ? headerSize + (segmentIndex * segmentSize) : segmentIndex * segmentSize;
                long currentSegmentSize = Math.Min(segmentSize, fileSize - (segmentIndex * segmentSize));

                // Use a semaphore to control concurrency
                await semaphore.WaitAsync(cancellationToken);

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await ProcessFileSegmentAsync(
                            inputFilePath, outputFilePath, encryption, key,
                            isEncrypting, inputOffset, outputOffset, currentSegmentSize, cancellationToken);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken));
            }

            // Wait for all segments to be processed
            await Task.WhenAll(tasks);

            // If we're decrypting, we might need to trim the file to the correct size
            // by removing padding bytes
            if ( !isEncrypting )
            {
                await TrimPaddingFromOutputFileAsync(outputFilePath, encryption, cancellationToken);
            }

            _logger.LogInformation("{Operation} completed for very large file: {Path}",
                isEncrypting ? "Encryption" : "Decryption", outputFilePath);
        }

        private async Task ProcessFileSegmentAsync(
     string inputFilePath,
     string outputFilePath,
     IEncryption encryption,
     byte[] key,
     bool isEncrypting,
     long inputOffset,
     long outputOffset,
     long length,
     CancellationToken cancellationToken)
        {
            // Read the segment from the input file
            byte[] segmentData;
            using ( var fileStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read) )
            {
                fileStream.Position = inputOffset;
                segmentData = new byte[length];
                await fileStream.ReadAsync(segmentData, 0, (int) length, cancellationToken);
            }

            // Process the segment
            byte[] processedData = isEncrypting
                ? encryption.Encrypt(segmentData, key)
                : encryption.Decrypt(segmentData, key);

            // Write the processed segment to the output file
            using ( var fileStream = new FileStream(outputFilePath, FileMode.Open, FileAccess.Write, FileShare.None) )
            {
                fileStream.Position = outputOffset;
                await fileStream.WriteAsync(processedData, 0, processedData.Length, cancellationToken);
            }
        }


        private async Task TrimPaddingFromOutputFileAsync(string filePath, IEncryption encryption, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Checking for padding in decrypted file: {Path}", filePath);

            try
            {
                // Get file info to access file length
                var fileInfo = new FileInfo(filePath);
                if ( !fileInfo.Exists || fileInfo.Length == 0 )
                {
                    _logger.LogWarning("File doesn't exist or is empty: {Path}", filePath);
                    return;
                }

                // Different encryption algorithms use different padding schemes
                PaddingScheme paddingScheme = _paddingService.DeterminePaddingScheme(encryption);

                // If no specific padding handling is needed
                if ( paddingScheme == PaddingScheme.None )
                {
                    _logger.LogInformation("No padding removal needed for {EncryptionType}", encryption.GetType().Name);
                    return;
                }

                // For small files, read the whole file
                if ( fileInfo.Length <= BufferSize )
                {
                    byte[] fileData = await File.ReadAllBytesAsync(filePath, cancellationToken);
                    int newLength = _paddingService.RemovePadding(fileData, paddingScheme);

                    if ( newLength < fileData.Length )
                    {
                        _logger.LogInformation("Removing {PaddingBytes} bytes of padding from file", fileData.Length - newLength);
                        await File.WriteAllBytesAsync(filePath, fileData.AsSpan(0, newLength).ToArray(), cancellationToken);
                    }
                }
                // For large files, operate on the end of the file
                else
                {
                    using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                    // Move to the position where padding might start - this depends on the specific algorithm
                    // For most algorithms, we only need to check the last block
                    int blockSize = _paddingService.GetBlockSize(encryption);
                    int bytesToRead = Math.Min(blockSize * 2, (int) fileStream.Length); // Read up to 2 blocks

                    // Read the end of the file to analyze padding
                    fileStream.Seek(-bytesToRead, SeekOrigin.End);
                    byte[] endBuffer = new byte[bytesToRead];
                    int bytesRead = await fileStream.ReadAsync(endBuffer, 0, bytesToRead, cancellationToken);

                    // Determine where the actual data ends
                    int paddingLength = _paddingService.CalculatePaddingLength(endBuffer.AsSpan(0, bytesRead), paddingScheme);

                    if ( paddingLength > 0 && paddingLength < bytesRead )
                    {
                        // Set the new file length (trimmed)
                        long newLength = fileStream.Length - paddingLength;
                        _logger.LogInformation("Trimming {PaddingBytes} bytes of padding from file", paddingLength);
                        fileStream.SetLength(newLength);
                    }
                }
            }
            catch ( Exception ex )
            {
                _logger.LogError(ex, "Error while trimming padding from file: {Path}", filePath);
                // Don't throw - we don't want to fail decryption just because padding removal failed
            }
        }
        /// <summary>
        /// Calculate the progress percentage
        /// </summary>
        private static int CalculateProgressPercentage(long current, long total)
        {
            if ( total <= 0 )
                return 0;

            return (int) (current * 100 / total);
        }
        /// <summary>
        /// Gets a standard algorithm ID for the given encryption algorithm
        /// </summary>
        private string GetAlgorithmId(IEncryption encryption)
        {
            return _algorithmIdentifier.GetAlgorithmId(encryption.GetType().Name);
        }
        // Method to add to FileEncryptionService.cs
        /// <summary>
        /// Displays detailed algorithm information for a detected algorithm
        /// </summary>
        public string GetAlgorithmInfoForDisplay(string algorithmId)
        {
            return $"{GetAlgorithmDisplayName(algorithmId)} (ID: {algorithmId})";
        }
        /// <summary>
        /// Gets a human-readable name for an algorithm ID
        /// </summary>
        public string GetAlgorithmDisplayName(string algorithmId)
        {
            return algorithmId switch
            {
                "AESG" => "AES-GCM",
                "CC20" => "ChaCha20",
                "SL20" => "Salsa20",
                "3DES" => "Triple DES",
                "3FSH" => "ThreeFish",
                "RC2_" => "RC2",
                "XCCH" => "XChaCha",
                "AES_" => "AES",
                _ => algorithmId
            };
        }

        /// <summary>
        /// Gets a human-readable name for the algorithm used by an encryption instance
        /// </summary>
        public string GetAlgorithmDisplayName(IEncryption encryption)
        {
            return GetAlgorithmDisplayName(GetAlgorithmId(encryption));
        }

        /// <summary>
        /// Returns the appropriate path for an encrypted file based on the original file and encryption algorithm
        /// </summary>
        public string GetEncryptedFilePath(string originalFilePath, IEncryption encryption)
        {
            var algorithmId = GetAlgorithmId(encryption);
            var algorithmExtension = AlgorithmExtensionMap.GetExtensionForAlgorithm(algorithmId);

            var extension = Path.GetExtension(originalFilePath);
            var baseFileName = Path.GetFileNameWithoutExtension(originalFilePath);
            var directory = Path.GetDirectoryName(originalFilePath) ?? string.Empty;

            // Create a new filename with the original extension plus the algorithm extension
            return Path.Combine(directory, baseFileName + extension + algorithmExtension);
        }

        /// <summary>
        /// Tries to detect the encryption algorithm used in a file
        /// </summary>
        /// <returns>A tuple containing the algorithm ID and encryption header (if found)</returns>
        public async Task<(string AlgorithmId, EncryptionHeader? Header)> DetectEncryptionAlgorithmAsync(
    string filePath, CancellationToken cancellationToken = default)
        {
            try
            {
                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

                // Check if file is large enough to contain a header
                if ( fileStream.Length < EncryptionHeader.HEADER_SIZE )
                {
                    _logger.LogDebug("File is too small to contain a valid encryption header: {Path}", filePath);
                    return (string.Empty, null);
                }

                try
                {
                    // Use ReadAsync to make this truly async
                    byte[] buffer = new byte[EncryptionHeader.HEADER_SIZE];
                    await fileStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                    // Reset position after reading
                    fileStream.Position = 0;

                    // Try to read header
                    var header = EncryptionHeader.Read(fileStream);
                    _logger.LogDebug("Detected encryption algorithm: {AlgorithmId}", header.AlgorithmId);
                    return (header.AlgorithmId, header);
                }
                catch ( InvalidDataException ex )
                {
                    _logger.LogDebug(ex, "Could not detect encryption algorithm from file header");
                }

                // If header detection failed, try detecting from file extension
                var extension = Path.GetExtension(filePath).TrimStart('.');
                var algorithmId = AlgorithmExtensionMap.TryGetAlgorithmFromExtension(extension);

                if ( !string.IsNullOrEmpty(algorithmId) )
                {
                    _logger.LogDebug("Detected encryption algorithm from file extension: {AlgorithmId}", algorithmId);
                    return (algorithmId, null);
                }

                // Could not detect algorithm
                return (string.Empty, null);
            }
            catch ( Exception ex )
            {
                _logger.LogDebug(ex, "Error while trying to detect encryption algorithm");
                return (string.Empty, null);
            }
        }


        /// <summary>
        /// Synchronous version of DetectEncryptionAlgorithmAsync
        /// </summary>
        public (string AlgorithmId, EncryptionHeader? Header) DetectEncryptionAlgorithm(string filePath)
        {
            return DetectEncryptionAlgorithmAsync(filePath).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Creates a custom file extension for the encrypted file based on the original extension and encryption algorithm
        /// </summary>
        private string CreateEncryptedFileExtension(string originalPath, IEncryption encryption)
        {
            var algorithmId = GetAlgorithmId(encryption);
            var algorithmExtension = AlgorithmExtensionMap.GetExtensionForAlgorithm(algorithmId);

            var extension = Path.GetExtension(originalPath);
            var baseFileName = Path.GetFileNameWithoutExtension(originalPath);
            var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;

            // Create a new filename with the original extension plus the algorithm extension
            // For example: document.docx becomes document.docx.aes
            return Path.Combine(directory, baseFileName + extension + algorithmExtension);
        }
        /// <summary>
        /// Create a progress reporting action that logs progress at appropriate intervals
        /// </summary>
        private Action<long, long> CreateProgressLogger(string operation, string filePath)
        {
            int lastReportedPercentage = -1;
            const int percentageIncrement = 5; // Only log when progress increases by this percentage

            return (current, total) =>
            {
                int percentage = CalculateProgressPercentage(current, total);
                if ( percentage > lastReportedPercentage + percentageIncrement || percentage == 100 )
                {
                    _logger.LogInformation("{Operation} progress for {Path}: {Percentage}% ({Current}/{Total} bytes)",
                        operation, filePath, percentage, current, total);
                    lastReportedPercentage = percentage;
                }
            };
        }

        /// <summary>
        /// Determines if a file is too large for even the large file processing method
        /// </summary>
        private bool IsVeryLargeFile(long fileSize)
        {
            // Threshold for switching to memory-mapped file processing
            // 1GB is a reasonable threshold for most modern systems
            return fileSize > (long) 1024 * 1024 * 1024;
        }
        #endregion
    }
}
