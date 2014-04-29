#region License
/*
MIT License
Copyright © 2006 The Mono.Xna Team

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
#endregion License

using System;
using System.IO;

using Microsoft.Xna.Framework;

namespace Microsoft.Xna.Framework.Audio
{
    /// <summary>Represents a wave bank, which is a collection of wave files.</summary>
    public class WaveBank : IDisposable
    {
        internal SoundEffectInstance[] sounds;
        internal string BankName;

        struct Segment
        {
            public int Offset;
            public int Length;
        }

        struct WaveBankEntry
        {
            public int Format;
            public Segment PlayRegion;
            public Segment LoopRegion;
            public int FlagsAndDuration;
        }

        struct WaveBankHeader
        {
            public int Version;
            public Segment[] Segments;
        }

        struct WaveBankData
        {
            public int    Flags;                                // Bank flags
            public int    EntryCount;                           // Number of entries in the bank
            public string BankName;                             // Bank friendly name
            public int    EntryMetaDataElementSize;             // Size of each entry meta-data element, in bytes
            public int    EntryNameElementSize;                 // Size of each entry name element, in bytes
            public int    Alignment;                            // Entry alignment, in bytes
            public int    CompactFormat;                        // Format data for compact bank
            public int    BuildTime;                            // Build timestamp
        }
        
        private const int Flag_EntryNames = 0x00010000; // Bank includes entry names
        private const int Flag_Compact = 0x00020000; // Bank uses compact format
        private const int Flag_SyncDisabled = 0x00040000; // Bank is disabled for audition sync
        private const int Flag_SeekTables = 0x00080000; // Bank includes seek tables.
        private const int Flag_Mask = 0x000F0000;
        
        private const int MiniFormatTag_PCM = 0x0;
        private const int MiniFormatTag_XMA = 0x1;
        private const int MiniFormatTag_ADPCM = 0x2;
        private const int MiniForamtTag_WMA = 0x3;

        /// <summary>Initializes a new, in-memory instance of this class using a specified AudioEngine and path to a wave bank file.</summary>
        /// <param name="audioEngine">Instance of an AudioEngine to associate this wave bank with.</param>
        /// <param name="nonStreamingWaveBankFilename">Path to the wave bank file to load.</param>
        /// <remarks>This constructor generates an in-memory version of a wave bank. The entire wave bank contents are held in memory.</remarks>
        public WaveBank(AudioEngine audioEngine, string nonStreamingWaveBankFilename)
        {
            //XWB PARSING
            //Adapted from MonoXNA
            //Originally adaped from Luigi Auriemma's unxwb
			
            WaveBankHeader wavebankheader;
            WaveBankData wavebankdata;
            WaveBankEntry wavebankentry;

            wavebankdata.EntryNameElementSize = 0;
            wavebankdata.CompactFormat = 0;
            wavebankdata.Alignment = 0;
            wavebankdata.BuildTime = 0;

            wavebankentry.Format = 0;
            wavebankentry.PlayRegion.Length = 0;
            wavebankentry.PlayRegion.Offset = 0;

            int wavebank_offset = 0;

#if WINRT
			const char notSeparator = '/';
			const char separator = '\\';
#else
            const char notSeparator = '\\';
            var separator = Path.DirectorySeparatorChar;
#endif
			// Check for windows-style directory separator character
			nonStreamingWaveBankFilename = nonStreamingWaveBankFilename.Replace(notSeparator, separator);

#if !ANDROID
            BinaryReader reader = new BinaryReader(TitleContainer.OpenStream(nonStreamingWaveBankFilename));
#else 
			Stream stream = Game.Activity.Assets.Open(nonStreamingWaveBankFilename);
			MemoryStream ms = new MemoryStream();
			stream.CopyTo( ms );
			stream.Close();
			ms.Position = 0;
			BinaryReader reader = new BinaryReader(ms);
#endif
			reader.ReadBytes(4);

            wavebankheader.Version = reader.ReadInt32();

            int last_segment = 4;
            //if (wavebankheader.Version == 1) goto WAVEBANKDATA;
            if (wavebankheader.Version <= 3) last_segment = 3;
            if (wavebankheader.Version >= 42) reader.ReadInt32();    // skip HeaderVersion

            wavebankheader.Segments = new Segment[5];

            for (int i = 0; i <= last_segment; i++)
            {
                wavebankheader.Segments[i].Offset = reader.ReadInt32();
                wavebankheader.Segments[i].Length = reader.ReadInt32();
            }

            reader.BaseStream.Seek(wavebankheader.Segments[0].Offset, SeekOrigin.Begin);

            //WAVEBANKDATA:

            wavebankdata.Flags = reader.ReadInt32();
            wavebankdata.EntryCount = reader.ReadInt32();

            if ((wavebankheader.Version == 2) || (wavebankheader.Version == 3))
            {
                wavebankdata.BankName = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(16),0,16).Replace("\0", "");
            }
            else
            {
                wavebankdata.BankName = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(64),0,64).Replace("\0", "");
            }

            BankName = wavebankdata.BankName;

            if (wavebankheader.Version == 1)
            {
                //wavebank_offset = (int)ftell(fd) - file_offset;
                wavebankdata.EntryMetaDataElementSize = 20;
            }
            else
            {
                wavebankdata.EntryMetaDataElementSize = reader.ReadInt32();
                wavebankdata.EntryNameElementSize = reader.ReadInt32();
                wavebankdata.Alignment = reader.ReadInt32();
                wavebank_offset = wavebankheader.Segments[1].Offset; //METADATASEGMENT
            }

            if ((wavebankdata.Flags & Flag_Compact) != 0)
            {
                reader.ReadInt32(); // compact_format
            }

            int playregion_offset = wavebankheader.Segments[last_segment].Offset;
            if (playregion_offset == 0)
            {
                playregion_offset =
                    wavebank_offset +
                    (wavebankdata.EntryCount * wavebankdata.EntryMetaDataElementSize);
            }
            
            int segidx_entry_name = 2;
            if (wavebankheader.Version >= 42) segidx_entry_name = 3;
            
            if ((wavebankheader.Segments[segidx_entry_name].Offset != 0) &&
                (wavebankheader.Segments[segidx_entry_name].Length != 0))
            {
                if (wavebankdata.EntryNameElementSize == -1) wavebankdata.EntryNameElementSize = 0;
                byte[] entry_name = new byte[wavebankdata.EntryNameElementSize + 1];
                entry_name[wavebankdata.EntryNameElementSize] = 0;
            }

            sounds = new SoundEffectInstance[wavebankdata.EntryCount];

            for (int current_entry = 0; current_entry < wavebankdata.EntryCount; current_entry++)
            {
                reader.BaseStream.Seek(wavebank_offset, SeekOrigin.Begin);
                //SHOWFILEOFF;

                //memset(&wavebankentry, 0, sizeof(wavebankentry));
				wavebankentry.LoopRegion.Length = 0;
				wavebankentry.LoopRegion.Offset = 0;

                if ((wavebankdata.Flags & Flag_Compact) != 0)
                {
                    int len = reader.ReadInt32();
                    wavebankentry.Format = wavebankdata.CompactFormat;
                    wavebankentry.PlayRegion.Offset = (len & ((1 << 21) - 1)) * wavebankdata.Alignment;
                    wavebankentry.PlayRegion.Length = (len >> 21) & ((1 << 11) - 1);

                    // workaround because I don't know how to handke the deviation length
                    reader.BaseStream.Seek(wavebank_offset + wavebankdata.EntryMetaDataElementSize, SeekOrigin.Begin);

                    //MYFSEEK(wavebank_offset + wavebankdata.dwEntryMetaDataElementSize); // seek to the next
                    if (current_entry == (wavebankdata.EntryCount - 1))
                    {              // the last track
                        len = wavebankheader.Segments[last_segment].Length;
                    }
                    else
                    {
                        len = ((reader.ReadInt32() & ((1 << 21) - 1)) * wavebankdata.Alignment);
                    }
                    wavebankentry.PlayRegion.Length =
                        len -                               // next offset
                        wavebankentry.PlayRegion.Offset;  // current offset
                    goto wavebank_handle;
                }

                if (wavebankheader.Version == 1)
                {
                    wavebankentry.Format = reader.ReadInt32();
                    wavebankentry.PlayRegion.Offset = reader.ReadInt32();
                    wavebankentry.PlayRegion.Length = reader.ReadInt32();
                    wavebankentry.LoopRegion.Offset = reader.ReadInt32();
                    wavebankentry.LoopRegion.Length = reader.ReadInt32();
                }
                else
                {
                    if (wavebankdata.EntryMetaDataElementSize >= 4) wavebankentry.FlagsAndDuration = reader.ReadInt32();
                    if (wavebankdata.EntryMetaDataElementSize >= 8) wavebankentry.Format = reader.ReadInt32();
                    if (wavebankdata.EntryMetaDataElementSize >= 12) wavebankentry.PlayRegion.Offset = reader.ReadInt32();
                    if (wavebankdata.EntryMetaDataElementSize >= 16) wavebankentry.PlayRegion.Length = reader.ReadInt32();
                    if (wavebankdata.EntryMetaDataElementSize >= 20) wavebankentry.LoopRegion.Offset = reader.ReadInt32();
                    if (wavebankdata.EntryMetaDataElementSize >= 24) wavebankentry.LoopRegion.Length = reader.ReadInt32();
                }

                if (wavebankdata.EntryMetaDataElementSize < 24)
                {                              // work-around
                    if (wavebankentry.PlayRegion.Length != 0)
                    {
                        wavebankentry.PlayRegion.Length = wavebankheader.Segments[last_segment].Length;
                    }

                }// else if(wavebankdata.EntryMetaDataElementSize > sizeof(WaveBankEntry)) {    // skip unused fields
            //   MYFSEEK(wavebank_offset + wavebankdata.EntryMetaDataElementSize);
            //}

                wavebank_handle:
                wavebank_offset += wavebankdata.EntryMetaDataElementSize;
                wavebankentry.PlayRegion.Offset += playregion_offset;
                
                // Parse WAVEBANKMINIWAVEFORMAT
                
                int codec;
                int chans;
                int rate;
                int align;
                //int bits;

                if (wavebankheader.Version == 1)
                {         // I'm not 100% sure if the following is correct
                    // version 1:
                    // 1 00000000 000101011000100010 0 001 0
                    // | |         |                 | |   |
                    // | |         |                 | |   wFormatTag
                    // | |         |                 | nChannels
                    // | |         |                 ???
                    // | |         nSamplesPerSec
                    // | wBlockAlign
                    // wBitsPerSample

                    codec = (wavebankentry.Format) & ((1 << 1) - 1);
                    chans = (wavebankentry.Format >> (1)) & ((1 << 3) - 1);
                    rate = (wavebankentry.Format >> (1 + 3 + 1)) & ((1 << 18) - 1);
                    align = (wavebankentry.Format >> (1 + 3 + 1 + 18)) & ((1 << 8) - 1);
                    //bits = (wavebankentry.Format >> (1 + 3 + 1 + 18 + 8)) & ((1 << 1) - 1);

                    /*} else if(wavebankheader.dwVersion == 23) { // I'm not 100% sure if the following is correct
                        // version 23:
                        // 1000000000 001011101110000000 001 1
                        // | |        |                  |   |
                        // | |        |                  |   ???
                        // | |        |                  nChannels?
                        // | |        nSamplesPerSec
                        // | ???
                        // !!!UNKNOWN FORMAT!!!

                        //codec = -1;
                        //chans = (wavebankentry.Format >>  1) & ((1 <<  3) - 1);
                        //rate  = (wavebankentry.Format >>  4) & ((1 << 18) - 1);
                        //bits  = (wavebankentry.Format >> 31) & ((1 <<  1) - 1);
                        codec = (wavebankentry.Format                    ) & ((1 <<  1) - 1);
                        chans = (wavebankentry.Format >> (1)             ) & ((1 <<  3) - 1);
                        rate  = (wavebankentry.Format >> (1 + 3)         ) & ((1 << 18) - 1);
                        align = (wavebankentry.Format >> (1 + 3 + 18)    ) & ((1 <<  9) - 1);
                        bits  = (wavebankentry.Format >> (1 + 3 + 18 + 9)) & ((1 <<  1) - 1); */

                }
                else
                {
                    // 0 00000000 000111110100000000 010 01
                    // | |        |                  |   |
                    // | |        |                  |   wFormatTag
                    // | |        |                  nChannels
                    // | |        nSamplesPerSec
                    // | wBlockAlign
                    // wBitsPerSample

                    codec = (wavebankentry.Format) & ((1 << 2) - 1);
                    chans = (wavebankentry.Format >> (2)) & ((1 << 3) - 1);
                    rate = (wavebankentry.Format >> (2 + 3)) & ((1 << 18) - 1);
                    align = (wavebankentry.Format >> (2 + 3 + 18)) & ((1 << 8) - 1);
                    //bits = (wavebankentry.Format >> (2 + 3 + 18 + 8)) & ((1 << 1) - 1);
                }
                
                reader.BaseStream.Seek(wavebankentry.PlayRegion.Offset, SeekOrigin.Begin);
                byte[] audiodata = reader.ReadBytes(wavebankentry.PlayRegion.Length);
                
                if (codec == MiniFormatTag_PCM) {
                    
                    //write PCM data into a wav
#if DIRECTX
                    
                    // TODO: Wouldn't storing a SoundEffectInstance like this
                    // result in the "parent" SoundEffect being garbage collected?
                    
					SharpDX.Multimedia.WaveFormat waveFormat = new SharpDX.Multimedia.WaveFormat(rate, chans);
                    var sfx = new SoundEffect(audiodata, 0, audiodata.Length, rate, (AudioChannels)chans, wavebankentry.LoopRegion.Offset, wavebankentry.LoopRegion.Length)
                        {
                            _format = waveFormat
                        };

					sounds[current_entry] = sfx.CreateInstance();
#else
					sounds[current_entry] = new SoundEffectInstance(audiodata, rate, chans);
#endif                    
                } else if (codec == MiniForamtTag_WMA) { //WMA or xWMA (or XMA2)
                    byte[] wmaSig = {0x30, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11, 0xa6, 0xd9, 0x0, 0xaa, 0x0, 0x62, 0xce, 0x6c};
                    
                    bool isWma = true;
                    for (int i=0; i<wmaSig.Length; i++) {
                        if (wmaSig[i] != audiodata[i]) {
                            isWma = false;
                            break;
                        }
                    }
                    
                    //Let's support m4a data as well for convenience
                    byte[][] m4aSigs = new byte[][] {
                        new byte[] {0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x4D, 0x34, 0x41, 0x20, 0x00, 0x00, 0x02, 0x00},
                        new byte[] {0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70, 0x4D, 0x34, 0x41, 0x20, 0x00, 0x00, 0x00, 0x00}
                    };
                    
                    bool isM4a = false;
                    for (int i=0; i<m4aSigs.Length; i++) {    
                        byte[] sig = m4aSigs[i];
                        bool matches = true;
                        for (int j=0; j<sig.Length; j++) {
                            if (sig[j] != audiodata[j]) {
                                matches = false;
                                break;
                            }
                        }
                        if (matches) {
                            isM4a = true;
                            break;
                        }
                    }
                    
                    if (isWma || isM4a) {
                        //WMA data can sometimes be played directly
#if DIRECTX
                        throw new NotImplementedException();
#elif !WINRT
                        //hack - NSSound can't play non-wav from data, we have to give a filename
                        string filename = Path.GetTempFileName();
                        if (isWma) {
                            filename = filename.Replace(".tmp", ".wma");
                        } else if (isM4a) {
                            filename = filename.Replace(".tmp", ".m4a");
                        }
                        using (var audioFile = File.Create(filename))
                        {
                            audioFile.Write(audiodata, 0, audiodata.Length);
                            audioFile.Seek(0, SeekOrigin.Begin);
       
                            sounds[current_entry] = SoundEffect.FromStream(audioFile).CreateInstance();
                        }
#else
						throw new NotImplementedException();
#endif
                    } else {
                        //An xWMA or XMA2 file. Can't be played atm :(
                        throw new NotImplementedException();
                    }
#if !DIRECTX
                /* DirectX platforms can use XAudio2 to stream MSADPCM natively.
                 * This code is cross-platform, but the problem is that it just
                 * decodes ALL of the wavedata here. For XAudio2 in particular,
                 * this is probably ludicrous.
                 *
                 * You need to write a DIRECTX ADPCM reader that just loads this
                 * into the SoundEffect. No decoding should be necessary.
                 * -flibit
                 */
                } else if (codec == MiniFormatTag_ADPCM) {
                    using (MemoryStream dataStream = new MemoryStream(audiodata)) {
                        using (BinaryReader source = new BinaryReader(dataStream)) {
                            sounds[current_entry] = new SoundEffectInstance(
                                MSADPCMToPCM.MSADPCM_TO_PCM(source, (short) chans, (short) align),
                                rate,
                                chans
                            );
                        }
                    }
#endif
                } else {
                    throw new NotImplementedException();
                }
                
            }
			
			audioEngine.Wavebanks[BankName] = this;
        }
		
        /// <summary>
        /// Initializes a new, streaming instance of this class, using a provided AudioEngine and streaming wave bank parameters.
        /// </summary>
        /// <param name="audioEngine">Instance of an AudioEngine to associate this wave bank with.</param>
        /// <param name="streamingWaveBankFilename">Path to the wave bank file to stream from.</param>
        /// <param name="offset">Offset within the wave bank data file. This offset must be DVD sector aligned.</param>
        /// <param name="packetsize">Stream packet size, in sectors, to use for each stream. The minimum value is 2.</param>
        /// <remarks>
        /// <para>This constructor constructs a streaming wave bank whose contents are streamed from storage as needed.</para>
        /// <para>When setting packetsize, note that the size of a DVD sector is 2,048 bytes. Therefore, setting this value to 2 would result in a packet size of 4,096 bytes. Setting it to 3 would specify packets of 6,144 bytes, setting it to 4 would specify packets of 8,192 bytes, and so on. The optimal DVD size is a multiple of 16 (1 DVD block = 16 DVD sectors).</para>
        /// <para>After creating a streaming wave bank, you must call Update at least once from the AudioEngine that was used to create the streaming wave bank before attempting to play a wave from the wave bank. This properly prepares the wave bank for use. Attempts to play waves out of any wave bank before the wave bank is prepared will result in an error.</para>
        /// </remarks>
		public WaveBank(AudioEngine audioEngine, string streamingWaveBankFilename, int offset, short packetsize)
			: this(audioEngine, streamingWaveBankFilename)
		{
			if (offset != 0) {
				throw new NotImplementedException();
			}
		}

		#region IDisposable implementation
		public void Dispose ()
		{
			throw new NotImplementedException ();
		}
		#endregion
    }
}

