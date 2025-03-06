﻿using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Versioning;
using Scuttle.Encrypt.BernsteinCore;
using Scuttle.Encrypt.BernSteinCore;
using Scuttle.Helpers;

namespace Scuttle.Encrypt.Strategies.ChaCha20
{
    /// <summary>
    /// AVX2-optimized implementation of ChaCha20 that processes two blocks in parallel
    /// </summary>
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal class ChaCha20Avx2Strategy : BaseChaCha20Strategy
    {
        public override int Priority => 300; // Highest priority
        public override string Description => "AVX2 SIMD Implementation (2x parallelism)";

        protected override void ProcessChunk(ReadOnlySpan<byte> inputChunk, byte[] key, ReadOnlySpan<byte> nonce, Span<byte> outputChunk)
        {
            int position = 0;
            uint counter = 0;

            // We can process two 64-byte blocks at once with AVX2
            byte[] blockBytes = ArrayPool<byte>.Shared.Rent(128); // 2 * ChaChaBlockSize

            try
            {
                // Initialize state
                Span<uint> initialState = stackalloc uint[16];

                // Initialize constants
                initialState[0] = ChaChaConstants.StateConstants[0];
                initialState[1] = ChaChaConstants.StateConstants[1];
                initialState[2] = ChaChaConstants.StateConstants[2];
                initialState[3] = ChaChaConstants.StateConstants[3];

                // Set key
                for ( int i = 0; i < 8; i++ )
                {
                    initialState[4 + i] = BitConverter.ToUInt32(key.AsSpan(i * 4, 4));
                }

                // Set nonce
                initialState[13] = BitConverter.ToUInt32(nonce.Slice(0, 4));
                initialState[14] = BitConverter.ToUInt32(nonce.Slice(4, 4));
                initialState[15] = BitConverter.ToUInt32(nonce.Slice(8, 4));

                // Process pairs of blocks as long as we have at least 1 full block
                while ( position + ChaChaBlockSize <= inputChunk.Length )
                {
                    // Set counter for this block pair
                    initialState[12] = counter;

                    // Process two blocks at once using AVX2
                    ProcessTwoBlocksAvx2(initialState, blockBytes, counter);

                    // XOR the first block with input
                    VectorOperations.ApplyXorSse2(
                        inputChunk.Slice(position, Math.Min(ChaChaBlockSize, inputChunk.Length - position)),
                        blockBytes.AsSpan(0, ChaChaBlockSize),
                        outputChunk.Slice(position, Math.Min(ChaChaBlockSize, inputChunk.Length - position)));
                    position += ChaChaBlockSize;

                    // XOR the second block if we have enough input remaining
                    if ( position < inputChunk.Length )
                    {
                        int remaining = Math.Min(ChaChaBlockSize, inputChunk.Length - position);
                        VectorOperations.ApplyXorSse2(
                            inputChunk.Slice(position, remaining),
                            blockBytes.AsSpan(ChaChaBlockSize, remaining),
                            outputChunk.Slice(position, remaining));
                        position += remaining;
                    }

                    // Update counter
                    counter += 2;
                }

                // Handle remaining data (less than a full block)
                if ( position < inputChunk.Length )
                {
                    // Process the last block using SSE2 (simpler for partial block)
                    initialState[12] = counter;

                    // Use SSE2 for the final partial block
                    Vector128<uint>[] state =
                    [
                        Vector128.Create(initialState[0], initialState[1], initialState[2], initialState[3]),
                        Vector128.Create(initialState[4], initialState[5], initialState[6], initialState[7]),
                        Vector128.Create(initialState[8], initialState[9], initialState[10], initialState[11]),
                        Vector128.Create(initialState[12], initialState[13], initialState[14], initialState[15]),
                    ];

                    // Create working copy
                    Vector128<uint>[] working = new Vector128<uint>[4];
                    state.CopyTo(working, 0);

                    // Apply ChaCha20 rounds
                    for ( int i = 0; i < 10; i++ )
                    {
                        ChaChaUtils.ChaChaRoundSse2(ref working[0], ref working[1], ref working[2], ref working[3]);
                    }

                    // Add original state
                    working[0] = Sse2.Add(working[0], state[0]);
                    working[1] = Sse2.Add(working[1], state[1]);
                    working[2] = Sse2.Add(working[2], state[2]);
                    working[3] = Sse2.Add(working[3], state[3]);

                    // Store to buffer
                    VectorOperations.StoreVector128(working[0], blockBytes.AsSpan(0));
                    VectorOperations.StoreVector128(working[1], blockBytes.AsSpan(16));
                    VectorOperations.StoreVector128(working[2], blockBytes.AsSpan(32));
                    VectorOperations.StoreVector128(working[3], blockBytes.AsSpan(48));

                    // XOR with input to produce output
                    int bytesToProcess = inputChunk.Length - position;
                    VectorOperations.ApplyXorSse2(
                        inputChunk.Slice(position, bytesToProcess),
                        blockBytes.AsSpan(0, bytesToProcess),
                        outputChunk.Slice(position, bytesToProcess));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(blockBytes);
            }
        }

        private void ProcessTwoBlocksAvx2(Span<uint> state, byte[] output, uint counter)
        {
            // Create the state for the two blocks
            Vector256<uint>[] vstate = new Vector256<uint>[4];

            // First block has counter, second block has counter+1
            Span<uint> state2 = stackalloc uint[16];
            state.CopyTo(state2);
            state2[12] = counter + 1; // Increment counter for second block

            // Interleave the state for the two blocks into AVX2 registers
            vstate[0] = Vector256.Create(
                state[0], state[1], state[2], state[3],
                state2[0], state2[1], state2[2], state2[3]);

            vstate[1] = Vector256.Create(
                state[4], state[5], state[6], state[7],
                state2[4], state2[5], state2[6], state2[7]);

            vstate[2] = Vector256.Create(
                state[8], state[9], state[10], state[11],
                state2[8], state2[9], state2[10], state2[11]);

            vstate[3] = Vector256.Create(
                state[12], state[13], state[14], state[15],
                state2[12], state2[13], state2[14], state2[15]);

            // Create working copy
            Vector256<uint>[] x = new Vector256<uint>[4];
            vstate.CopyTo(x, 0);

            // Main loop - 10 iterations of ChaCha20 rounds
            for ( int i = 0; i < 10; i++ )
            {
                // Column rounds
                QuarterRoundAvx2(ref x[0], ref x[1], ref x[2], ref x[3]);

                // Diagonal rounds (with appropriate shuffling)
                x[1] = Avx2.PermuteVar8x32(x[1].AsInt32(), Vector256.Create(1, 2, 3, 0, 5, 6, 7, 4)).AsUInt32();
                x[2] = Avx2.PermuteVar8x32(x[2].AsInt32(), Vector256.Create(2, 3, 0, 1, 6, 7, 4, 5)).AsUInt32();
                x[3] = Avx2.PermuteVar8x32(x[3].AsInt32(), Vector256.Create(3, 0, 1, 2, 7, 4, 5, 6)).AsUInt32();

                // Diagonal rounds
                QuarterRoundAvx2(ref x[0], ref x[1], ref x[2], ref x[3]);

                // Restore original positions
                x[1] = Avx2.PermuteVar8x32(x[1].AsInt32(), Vector256.Create(3, 0, 1, 2, 7, 4, 5, 6)).AsUInt32();
                x[2] = Avx2.PermuteVar8x32(x[2].AsInt32(), Vector256.Create(2, 3, 0, 1, 6, 7, 4, 5)).AsUInt32();
                x[3] = Avx2.PermuteVar8x32(x[3].AsInt32(), Vector256.Create(1, 2, 3, 0, 5, 6, 7, 4)).AsUInt32();
            }

            // Add initial state back to working state
            x[0] = Avx2.Add(x[0], vstate[0]);
            x[1] = Avx2.Add(x[1], vstate[1]);
            x[2] = Avx2.Add(x[2], vstate[2]);
            x[3] = Avx2.Add(x[3], vstate[3]);

            // Deinterleave the blocks and store them sequentially
            StoreDeinterleavedBlocks(x, output);
        }

        /// <summary>
        /// Performs a quarter round operation on four 256-bit vectors using AVX2
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void QuarterRoundAvx2(ref Vector256<uint> a, ref Vector256<uint> b, ref Vector256<uint> c, ref Vector256<uint> d)
        {
            // a += b; d ^= a; d <<<= 16;
            a = Avx2.Add(a, b);
            d = Avx2.Xor(d, a);
            d = Avx2.Xor(
                Avx2.ShiftLeftLogical(d, 16),
                Avx2.ShiftRightLogical(d, 32 - 16)
            );

            // c += d; b ^= c; b <<<= 12;
            c = Avx2.Add(c, d);
            b = Avx2.Xor(b, c);
            b = Avx2.Xor(
                Avx2.ShiftLeftLogical(b, 12),
                Avx2.ShiftRightLogical(b, 32 - 12)
            );

            // a += b; d ^= a; d <<<= 8;
            a = Avx2.Add(a, b);
            d = Avx2.Xor(d, a);
            d = Avx2.Xor(
                Avx2.ShiftLeftLogical(d, 8),
                Avx2.ShiftRightLogical(d, 32 - 8)
            );

            // c += d; b ^= c; b <<<= 7;
            c = Avx2.Add(c, d);
            b = Avx2.Xor(b, c);
            b = Avx2.Xor(
                Avx2.ShiftLeftLogical(b, 7),
                Avx2.ShiftRightLogical(b, 32 - 7)
            );
        }

        /// <summary>
        /// Store two interleaved ChaCha20 blocks from AVX2 registers into sequential bytes
        /// </summary>
        private static void StoreDeinterleavedBlocks(Vector256<uint>[] x, byte[] output)
        {
            // Extract first block (first 4 lanes of each vector)
            Span<uint> block1 = stackalloc uint[16];
            block1[0] = x[0].GetElement(0);
            block1[1] = x[0].GetElement(1);
            block1[2] = x[0].GetElement(2);
            block1[3] = x[0].GetElement(3);
            block1[4] = x[1].GetElement(0);
            block1[5] = x[1].GetElement(1);
            block1[6] = x[1].GetElement(2);
            block1[7] = x[1].GetElement(3);
            block1[8] = x[2].GetElement(0);
            block1[9] = x[2].GetElement(1);
            block1[10] = x[2].GetElement(2);
            block1[11] = x[2].GetElement(3);
            block1[12] = x[3].GetElement(0);
            block1[13] = x[3].GetElement(1);
            block1[14] = x[3].GetElement(2);
            block1[15] = x[3].GetElement(3);

            // Extract second block (last 4 lanes of each vector)
            Span<uint> block2 = stackalloc uint[16];
            block2[0] = x[0].GetElement(4);
            block2[1] = x[0].GetElement(5);
            block2[2] = x[0].GetElement(6);
            block2[3] = x[0].GetElement(7);
            block2[4] = x[1].GetElement(4);
            block2[5] = x[1].GetElement(5);
            block2[6] = x[1].GetElement(6);
            block2[7] = x[1].GetElement(7);
            block2[8] = x[2].GetElement(4);
            block2[9] = x[2].GetElement(5);
            block2[10] = x[2].GetElement(6);
            block2[11] = x[2].GetElement(7);
            block2[12] = x[3].GetElement(4);
            block2[13] = x[3].GetElement(5);
            block2[14] = x[3].GetElement(6);
            block2[15] = x[3].GetElement(7);

            // Store blocks sequentially with proper endianness handling
            for ( int i = 0; i < 16; i++ )
            {
                EndianHelper.WriteUInt32ToBytes(block1[i], output.AsSpan(i * 4, 4));
                EndianHelper.WriteUInt32ToBytes(block2[i], output.AsSpan(64 + i * 4, 4));
            }
        }

        protected override void GenerateKeyStreamInternal(Span<uint> initialState, Span<byte> keyStream)
        {
            int position = 0;
            uint counter = initialState[12]; // Start with the initial counter value

            byte[] doubleBlockBytes = ArrayPool<byte>.Shared.Rent(128); // 2 * ChaChaBlockSize

            try
            {
                // Process two blocks at a time with AVX2
                while ( position + ChaChaBlockSize < keyStream.Length )
                {
                    // Process two blocks at once
                    ProcessTwoBlocksAvx2(initialState, doubleBlockBytes, counter);

                    // Copy to output
                    int bytesToCopy = Math.Min(128, keyStream.Length - position);
                    if ( bytesToCopy > ChaChaBlockSize )
                    {
                        doubleBlockBytes.AsSpan(0, ChaChaBlockSize).CopyTo(keyStream.Slice(position, ChaChaBlockSize));
                        position += ChaChaBlockSize;
                        int remainingBytes = bytesToCopy - ChaChaBlockSize;
                        doubleBlockBytes.AsSpan(ChaChaBlockSize, remainingBytes).CopyTo(keyStream.Slice(position, remainingBytes));
                        position += remainingBytes;
                    }
                    else
                    {
                        doubleBlockBytes.AsSpan(0, bytesToCopy).CopyTo(keyStream.Slice(position, bytesToCopy));
                        position += bytesToCopy;
                    }

                    // Update counter
                    counter += 2;
                    initialState[12] = counter;
                }

                // Handle any remaining bytes with SSE2 (if less than a full block remaining)
                if ( position < keyStream.Length )
                {
                    byte[] blockBytes = ArrayPool<byte>.Shared.Rent(ChaChaBlockSize);
                    try
                    {
                        // Use SSE2 for the final partial block
                        Vector128<uint>[] state = new Vector128<uint>[4];
                        initialState[12] = counter;  // Set current counter value
                        state[0] = Vector128.Create(initialState[0], initialState[1], initialState[2], initialState[3]);
                        state[1] = Vector128.Create(initialState[4], initialState[5], initialState[6], initialState[7]);
                        state[2] = Vector128.Create(initialState[8], initialState[9], initialState[10], initialState[11]);
                        state[3] = Vector128.Create(initialState[12], initialState[13], initialState[14], initialState[15]);

                        // Create working copy
                        Vector128<uint>[] working = new Vector128<uint>[4];
                        state.CopyTo(working, 0);

                        // Apply ChaCha20 rounds
                        for ( int i = 0; i < 10; i++ )
                        {
                            ChaChaUtils.ChaChaRoundSse2(ref working[0], ref working[1], ref working[2], ref working[3]);
                        }

                        // Add original state
                        working[0] = Sse2.Add(working[0], state[0]);
                        working[1] = Sse2.Add(working[1], state[1]);
                        working[2] = Sse2.Add(working[2], state[2]);
                        working[3] = Sse2.Add(working[3], state[3]);

                        // Store to buffer
                        VectorOperations.StoreVector128(working[0], blockBytes.AsSpan(0));
                        VectorOperations.StoreVector128(working[1], blockBytes.AsSpan(16));
                        VectorOperations.StoreVector128(working[2], blockBytes.AsSpan(32));
                        VectorOperations.StoreVector128(working[3], blockBytes.AsSpan(48));

                        // Copy to output
                        int bytesToCopy = keyStream.Length - position;
                        blockBytes.AsSpan(0, bytesToCopy).CopyTo(keyStream.Slice(position, bytesToCopy));

                        // Update counter in initialState for caller
                        initialState[12] = counter + 1;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(blockBytes);
                    }
                }
                else
                {
                    // Make sure counter is updated correctly when we processed blocks evenly
                    initialState[12] = counter;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(doubleBlockBytes);
            }
        }

        /// <summary>
        /// Check if AVX2 is supported on the current hardware
        /// </summary>
        public static bool IsSupported => Avx2.IsSupported;
    }
}
