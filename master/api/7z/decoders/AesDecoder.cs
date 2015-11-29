﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ManagedLzma.SevenZip
{
    internal sealed class AesArchiveDecoder : DecoderNode
    {
        private sealed class PasswordProvider : master._7zip.Legacy.IPasswordProvider
        {
            private readonly PasswordStorage mPassword;

            public PasswordProvider(PasswordStorage password)
            {
                mPassword = password;
            }

            string master._7zip.Legacy.IPasswordProvider.CryptoGetTextPassword()
            {
                return new string(mPassword.GetPassword());
            }
        }

        private sealed class InputStream : Stream
        {
            private ReaderNode mInput;
            private long mInputLength;

            public void SetInput(ReaderNode stream, long length)
            {
                if (stream == null)
                    throw new ArgumentNullException(nameof(stream));

                if (mInput != null)
                    throw new InvalidOperationException();

                mInput = stream;
                mInputLength = length;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (mInput == null)
                    throw new InvalidOperationException();

                return mInput.Read(buffer, offset, count);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;

            public override long Length
            {
                get { return mInputLength; }
            }

            public override long Position
            {
                get { throw new InvalidOperationException(); }
                set { throw new InvalidOperationException(); }
            }

            public override void Flush()
            {
                throw new InvalidOperationException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new InvalidOperationException();
            }

            public override void SetLength(long value)
            {
                throw new InvalidOperationException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new InvalidOperationException();
            }
        }

        private sealed class OutputStream : ReaderNode
        {
            private AesArchiveDecoder mOwner;
            public OutputStream(AesArchiveDecoder owner) { mOwner = owner; }
            public override void Dispose() { mOwner = null; }
            public override void Skip(int count) => mOwner.Skip(count);
            public override int Read(byte[] buffer, int offset, int count) => mOwner.Read(buffer, offset, count);
        }

        private InputStream mInput;
        private OutputStream mOutput;
        private long mLength;
        private long mPosition;

        private Stream mStream;
        private ICryptoTransform mDecoder;
        private byte[] mBuffer;
        private long mWritten;
        private long mLimit;
        private int mOffset;
        private int mEnding;
        private int mUnderflow;

        public AesArchiveDecoder(ImmutableArray<byte> settings, PasswordStorage password, long length)
        {
            if (password == null)
                throw new InvalidOperationException("Password required.");

            mInput = new InputStream();
            mOutput = new OutputStream(this);
            mLength = length;

            Initialize(mInput, settings.ToArray(), new PasswordProvider(password), length);
        }

        public override void Dispose()
        {
            mStream.Dispose();
            mDecoder.Dispose();

            throw new NotImplementedException();
        }

        public override void SetInputStream(int index, ReaderNode stream, long length)
        {
            if (index != 0)
                throw new ArgumentOutOfRangeException(nameof(index));

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            mInput.SetInput(stream, length);
        }

        public override ReaderNode GetOutputStream(int index)
        {
            if (index != 0)
                throw new ArgumentOutOfRangeException(nameof(index));

            return mOutput;
        }

        private void Skip(int count)
        {
            throw new NotImplementedException();
        }

        private int Read(byte[] buffer, int offset, int count)
        {
            var result = DecodeInto(buffer, offset, count);
            mPosition += result;
            return result;
        }

        private void Initialize(Stream input, byte[] info, master._7zip.Legacy.IPasswordProvider pass, long limit)
        {
            mStream = input;
            mLimit = limit;

            // The 7z AES encoder/decoder classes do not perform padding, instead they require the input stream to provide a multiple of 16 bytes.
            // If the exception below is thrown this means the 7z file is either corrupt or a newer 7z version has been published and we haven't updated yet.
            if (((uint)input.Length & 15) != 0)
                throw new NotSupportedException("7z requires AES streams to be properly padded.");

            int numCyclesPower;
            byte[] salt, seed;
            Init(info, out numCyclesPower, out salt, out seed);

            byte[] password = Encoding.Unicode.GetBytes(pass.CryptoGetTextPassword());
            byte[] key = InitKey(numCyclesPower, salt, password);

            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                mDecoder = aes.CreateDecryptor(key, seed);
            }

            mBuffer = new byte[4 << 10];
        }

        private int DecodeInto(byte[] buffer, int offset, int count)
        {
            if (count == 0 || mWritten == mLimit)
                return 0;

            if (mUnderflow > 0)
                return HandleUnderflow(buffer, offset, count);

            // Need at least 16 bytes to proceed.
            if (mEnding - mOffset < 16)
            {
                Buffer.BlockCopy(mBuffer, mOffset, mBuffer, 0, mEnding - mOffset);
                mEnding -= mOffset;
                mOffset = 0;

                do
                {
                    int read = mStream.Read(mBuffer, mEnding, mBuffer.Length - mEnding);
                    if (read == 0)
                    {
                        // We are not done decoding and have less than 16 bytes.
                        throw new EndOfStreamException();
                    }

                    mEnding += read;
                }
                while (mEnding - mOffset < 16);
            }

            // We shouldn't return more data than we are limited to.
            // Currently this is handled by forcing an underflow if
            // the stream length is not a multiple of the block size.
            if (count > mLimit - mWritten)
                count = (int)(mLimit - mWritten);

            // We cannot transform less than 16 bytes into the target buffer,
            // but we also cannot return zero, so we need to handle this.
            // We transform the data locally and use our own buffer as cache.
            if (count < 16)
                return HandleUnderflow(buffer, offset, count);

            if (count > mEnding - mOffset)
                count = mEnding - mOffset;

            // Otherwise we transform directly into the target buffer.
            int processed = mDecoder.TransformBlock(mBuffer, mOffset, count & ~15, buffer, offset);
            mOffset += processed;
            mWritten += processed;
            return processed;
        }

        private void Init(byte[] info, out int numCyclesPower, out byte[] salt, out byte[] iv)
        {
            byte bt = info[0];
            numCyclesPower = bt & 0x3F;

            if ((bt & 0xC0) == 0)
            {
                salt = new byte[0];
                iv = new byte[0];
                return;
            }

            int saltSize = (bt >> 7) & 1;
            int ivSize = (bt >> 6) & 1;
            if (info.Length == 1)
                throw new InvalidDataException();

            byte bt2 = info[1];
            saltSize += (bt2 >> 4);
            ivSize += (bt2 & 15);
            if (info.Length < 2 + saltSize + ivSize)
                throw new InvalidDataException();

            salt = new byte[saltSize];
            for (int i = 0; i < saltSize; i++)
                salt[i] = info[i + 2];

            iv = new byte[16];
            for (int i = 0; i < ivSize; i++)
                iv[i] = info[i + saltSize + 2];

            if (numCyclesPower > 24)
                throw new NotSupportedException();
        }

        internal static byte[] InitKey(int mNumCyclesPower, byte[] salt, byte[] pass)
        {
            if (mNumCyclesPower == 0x3F)
            {
                var key = new byte[32];

                int pos;
                for (pos = 0; pos < salt.Length; pos++)
                    key[pos] = salt[pos];
                for (int i = 0; i < pass.Length && pos < 32; i++)
                    key[pos++] = pass[i];

                return key;
            }
            else
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] counter = new byte[8];
                    long numRounds = 1L << mNumCyclesPower;
                    for (long round = 0; round < numRounds; round++)
                    {
                        sha.TransformBlock(salt, 0, salt.Length, null, 0);
                        sha.TransformBlock(pass, 0, pass.Length, null, 0);
                        sha.TransformBlock(counter, 0, 8, null, 0);

                        // This mirrors the counter so we don't have to convert long to byte[] each round.
                        // (It also ensures the counter is little endian, which BitConverter does not.)
                        for (int i = 0; i < 8; i++)
                            if (++counter[i] != 0)
                                break;
                    }

                    sha.TransformFinalBlock(counter, 0, 0);
                    return sha.Hash;
                }
            }
        }

        private int HandleUnderflow(byte[] buffer, int offset, int count)
        {
            // If this is zero we were called to create a new underflow buffer.
            // Just transform as much as possible so we can feed from it as long as possible.
            if (mUnderflow == 0)
            {
                int blockSize = (mEnding - mOffset) & ~15;
                mUnderflow = mDecoder.TransformBlock(mBuffer, mOffset, blockSize, mBuffer, mOffset);
            }

            if (count > mUnderflow)
                count = mUnderflow;

            Buffer.BlockCopy(mBuffer, mOffset, buffer, offset, count);
            mWritten += count;
            mOffset += count;
            mUnderflow -= count;
            return count;
        }
    }
}
