//! \file       ImageGCmp.cs
//! \date       2017 Dec 01
//! \brief      Nekotaro Game System compressed image format.
//
// Copyright (C) 2017 by morkt
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//

using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameRes.Utility;

namespace GameRes.Formats.Nekotaro
{
    [Export(typeof(ImageFormat))]
    public class GCmpFormat : ImageFormat
    {
        public override string         Tag { get { return "GCMP"; } }
        public override string Description { get { return "Nekotaro Game System image format"; } }
        public override uint     Signature { get { return 0x706D4347; } } // 'GCmp'

        public override ImageMetaData ReadMetaData (IBinaryStream file)
        {
            var header = file.ReadHeader (0x10);
            int bpp = header[12];
            if (bpp != 24 && bpp != 8 && bpp != 1)
                return null;
            return new ImageMetaData {
                Width = header.ToUInt16 (8),
                Height = header.ToUInt16 (10),
                BPP = bpp,
            };
        }

        public override ImageData Read (IBinaryStream file, ImageMetaData info)
        {
            using (var reader = new GCmpDecoder (file, info, this, true))
                return reader.Image;
        }

        public override void Write (Stream file, ImageData image)
        {
            throw new System.NotImplementedException ("GCmpFormat.Write not implemented");
        }
    }

    internal sealed class GCmpDecoder : IImageDecoder
    {
        IBinaryStream   m_input;
        ImageData       m_image;
        bool            m_should_dispose;

        public Stream            Source { get { return m_input.AsStream; } }
        public ImageFormat SourceFormat { get; private set; }
        public ImageMetaData       Info { get; private set; }
        public PixelFormat       Format { get; private set; }
        public BitmapPalette    Palette { get; private set; }
        public int               Stride { get; private set; }
        public ImageData          Image {
            get {
                if (null == m_image)
                {
                    var pixels = Unpack();
                    m_image = ImageData.CreateFlipped (Info, Format, Palette, pixels, Stride);
                }
                return m_image;
            }
        }

        public GCmpDecoder (IBinaryStream input, ImageMetaData info, ImageFormat source, bool leave_open = false)
        {
            m_input = input;
            Info = info;
            SourceFormat = source;
            m_should_dispose = !leave_open;
            if (info.BPP > 1)
                Stride = (int)info.Width * info.BPP / 8;
            else
                Stride = ((int)info.Width + 7) / 8;
        }

        public byte[] Unpack ()
        {
            m_input.Position = 0x10;
            if (24 == Info.BPP)
                return Unpack24bpp();
            else
                return Unpack8bpp();
        }

        byte[] Unpack24bpp ()
        {
            Format = PixelFormats.Bgr24;
            int pixel_count = (int)(Info.Width * Info.Height);
            var output = new byte[pixel_count * Info.BPP / 8 + 1];
            var frame = new byte[384];
            int dst = 0;
            int v19 = 0;
            while (pixel_count > 0)
            {
                int count, frame_pos, pixel;
                if (v19 != 0)
                {
                    pixel = m_input.ReadInt24();
                    count = 1;
                    frame_pos = 127;
                    --v19;
                }
                else
                {
                    count = m_input.ReadUInt8();
                    int lo = count & 0x1F;
                    if (0 != (count & 0x80))
                    {
                        count = ((byte)count >> 5) & 3;
                        if (count != 0)
                        {
                            frame_pos = lo;
                        }
                        else
                        {
                            count = lo << 1;
                            frame_pos = m_input.ReadUInt8();
                            if (0 != (frame_pos & 0x80))
                                ++count;
                            frame_pos &= 0x7F;
                        }
                        if (0 == count)
                        {
                            count = m_input.ReadInt32();
                        }
                        int fpos = 3 * frame_pos;
                        pixel = frame[fpos] | frame[fpos+1] << 8 | frame[fpos+2] << 16;
                    }
                    else
                    {
                        if (1 == count)
                        {
                            v19 = m_input.ReadUInt8() - 1;
                        }
                        else if (0 == count)
                        {
                            count = m_input.ReadInt32();
                        }
                        pixel = m_input.ReadInt24();
                        frame_pos = 127;
                    }
                }
                if (count > pixel_count)
                    count = pixel_count;
                pixel_count -= count;
                LittleEndian.Pack (pixel, output, dst);
                dst += 3;
                if (--count > 0)
                {
                    count *= 3;
                    Binary.CopyOverlapped (output, dst - 3, dst, count);
                    dst += count;
                }
                if (frame_pos != 0)
                    Buffer.BlockCopy (frame, 0, frame, 3, 3 * frame_pos);
                frame[0] = (byte)pixel;
                frame[1] = (byte)(pixel >> 8);
                frame[2] = (byte)(pixel >> 16);
            }
            return output;
        }

        byte[] Unpack8bpp ()
        {
            if (8 == Info.BPP)
            {
                Format = PixelFormats.Indexed8;
                Palette = DefaultPalette;
            }
            else
                Format = PixelFormats.BlackWhite;
            int pixel_count = (int)Info.Height * Stride;
            var output = new byte[pixel_count];
            int dst = 0;
            byte[] frame = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 0xFF };

            int count = pixel_count;
            int extra_count = pixel_count;
            while (pixel_count > 0)
            {
                byte pixel;
                int frame_pos;
                byte ctl = m_input.ReadUInt8();
                int hi = ctl >> 4;
                int lo = ctl & 0xF;
                if (hi != 0)
                {
                    frame_pos = hi - 1;
                    pixel = frame[frame_pos];
                    count = lo + 1;
                }
                else
                {
                    switch (lo)
                    {
                    default:
                        count = lo + 1;
                        break;
                    case 10:
                        count = m_input.ReadUInt8() + 11;
                        break;
                    case 11:
                        count = m_input.ReadUInt16() + 267;
                        break;
                    case 12:
                        count = m_input.ReadInt32() + 65803;
                        break;
                    case 13:
                        extra_count = 0x10;
                        count = m_input.ReadUInt8();
                        break;
                    case 14:
                        extra_count = 0x120;
                        count = m_input.ReadUInt16();
                        break;
                    case 15:
                        extra_count = 0x10130;
                        count = m_input.ReadInt32();
                        break;
                    }
                    pixel = m_input.ReadUInt8();
                    if (lo < 13)
                    {
                        frame_pos = 14;
                    }
                    else
                    {
                        lo = pixel & 0xF;
                        frame_pos = (pixel >> 4) - 1;
                        pixel = frame[frame_pos];
                        count = extra_count + 16 * count + lo + 1;
                    }
                }
                if (count > pixel_count)
                    count = pixel_count;
                pixel_count -= count;
                for (int i = 0; i < count; ++i)
                    output[dst++] = pixel;
                Buffer.BlockCopy (frame, 0, frame, 1, frame_pos);
                frame[0] = pixel;
            }
            return output;
        }

        static readonly BitmapPalette DefaultPalette = new BitmapPalette (
            new Color[] {
                Color.FromRgb (0x00, 0x00, 0x00),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0x22, 0x22, 0x22),
                Color.FromRgb (0x44, 0x44, 0x44),
                Color.FromRgb (0x55, 0x55, 0x55),
                Color.FromRgb (0x66, 0x66, 0x66),
                Color.FromRgb (0x77, 0x77, 0x77),
                Color.FromRgb (0x88, 0x88, 0x88),
                Color.FromRgb (0x99, 0x99, 0x99),
                Color.FromRgb (0xAA, 0xAA, 0xAA),
                Color.FromRgb (0xBB, 0xBB, 0xBB),
                Color.FromRgb (0xCC, 0xCC, 0xCC),
                Color.FromRgb (0xDD, 0xDD, 0xDD),
                Color.FromRgb (0xEE, 0xEE, 0xEE),
                Color.FromRgb (0x00, 0xFF, 0x00),
                Color.FromRgb (0x1C, 0x09, 0x05),
                Color.FromRgb (0x2F, 0x0A, 0x05),
                Color.FromRgb (0x4E, 0x04, 0x02),
                Color.FromRgb (0x41, 0x0C, 0x05),
                Color.FromRgb (0x29, 0x15, 0x36),
                Color.FromRgb (0x24, 0x22, 0x21),
                Color.FromRgb (0x6C, 0x07, 0x0D),
                Color.FromRgb (0x1F, 0x2D, 0x36),
                Color.FromRgb (0x4B, 0x21, 0x18),
                Color.FromRgb (0x5D, 0x1B, 0x0D),
                Color.FromRgb (0x8B, 0x00, 0x36),
                Color.FromRgb (0x8E, 0x06, 0x16),
                Color.FromRgb (0x7E, 0x11, 0x0F),
                Color.FromRgb (0x09, 0x44, 0x64),
                Color.FromRgb (0x48, 0x2C, 0x4B),
                Color.FromRgb (0x38, 0x37, 0x3A),
                Color.FromRgb (0x3A, 0x24, 0x88),
                Color.FromRgb (0x74, 0x23, 0x12),
                Color.FromRgb (0x0D, 0x53, 0x29),
                Color.FromRgb (0x22, 0x34, 0x86),
                Color.FromRgb (0xB1, 0x03, 0x2A),
                Color.FromRgb (0x4B, 0x37, 0x28),
                Color.FromRgb (0x64, 0x30, 0x28),
                Color.FromRgb (0x32, 0x4A, 0x2D),
                Color.FromRgb (0x9B, 0x17, 0x20),
                Color.FromRgb (0xB0, 0x10, 0x10),
                Color.FromRgb (0x3D, 0x19, 0xCC),
                Color.FromRgb (0x1B, 0x38, 0xB2),
                Color.FromRgb (0x97, 0x25, 0x13),
                Color.FromRgb (0x30, 0x4C, 0x5E),
                Color.FromRgb (0x77, 0x38, 0x22),
                Color.FromRgb (0xD3, 0x0B, 0x1F),
                Color.FromRgb (0x01, 0x69, 0x65),
                Color.FromRgb (0x5F, 0x46, 0x33),
                Color.FromRgb (0x4B, 0x4D, 0x4F),
                Color.FromRgb (0xB6, 0x1B, 0x34),
                Color.FromRgb (0x0A, 0x74, 0x34),
                Color.FromRgb (0xBB, 0x26, 0x11),
                Color.FromRgb (0xED, 0x0B, 0x26),
                Color.FromRgb (0x2F, 0x52, 0x97),
                Color.FromRgb (0x49, 0x20, 0xFB),
                Color.FromRgb (0x89, 0x44, 0x15),
                Color.FromRgb (0x67, 0x46, 0x65),
                Color.FromRgb (0x06, 0x76, 0x72),
                Color.FromRgb (0x93, 0x3F, 0x2D),
                Color.FromRgb (0x3F, 0x65, 0x49),
                Color.FromRgb (0x6D, 0x52, 0x3B),
                Color.FromRgb (0x88, 0x4C, 0x38),
                Color.FromRgb (0xE5, 0x26, 0x17),
                Color.FromRgb (0xA6, 0x47, 0x1D),
                Color.FromRgb (0x43, 0x68, 0x7D),
                Color.FromRgb (0x23, 0x50, 0xE8),
                Color.FromRgb (0xE3, 0x24, 0x43),
                Color.FromRgb (0x94, 0x56, 0x1C),
                Color.FromRgb (0x60, 0x63, 0x64),
                Color.FromRgb (0xBC, 0x3E, 0x49),
                Color.FromRgb (0x06, 0x9C, 0x45),
                Color.FromRgb (0xC4, 0x44, 0x24),
                Color.FromRgb (0xB1, 0x55, 0x2B),
                Color.FromRgb (0x8D, 0x60, 0x53),
                Color.FromRgb (0x63, 0x46, 0xFB),
                Color.FromRgb (0x7B, 0x6C, 0x61),
                Color.FromRgb (0x91, 0x57, 0x97),
                Color.FromRgb (0xAA, 0x5A, 0x4C),
                Color.FromRgb (0x49, 0x7E, 0xA0),
                Color.FromRgb (0xF8, 0x3C, 0x29),
                Color.FromRgb (0xA9, 0x67, 0x20),
                Color.FromRgb (0xC9, 0x56, 0x36),
                Color.FromRgb (0xA2, 0x6A, 0x3E),
                Color.FromRgb (0xBF, 0x56, 0x6C),
                Color.FromRgb (0x77, 0x7A, 0x7B),
                Color.FromRgb (0x5D, 0x79, 0xD2),
                Color.FromRgb (0xCC, 0x62, 0x44),
                Color.FromRgb (0xA3, 0x75, 0x63),
                Color.FromRgb (0xDE, 0x60, 0x31),
                Color.FromRgb (0xB5, 0x79, 0x23),
                Color.FromRgb (0x45, 0x80, 0xF5),
                Color.FromRgb (0xFD, 0x56, 0x29),
                Color.FromRgb (0xEE, 0x52, 0x64),
                Color.FromRgb (0x8C, 0x83, 0x6E),
                Color.FromRgb (0xCD, 0x70, 0x2A),
                Color.FromRgb (0xC4, 0x6E, 0x53),
                Color.FromRgb (0x86, 0x87, 0x87),
                Color.FromRgb (0x5E, 0x95, 0xAC),
                Color.FromRgb (0x7D, 0x6C, 0xFD),
                Color.FromRgb (0x36, 0xC5, 0x22),
                Color.FromRgb (0xAC, 0x6F, 0xB2),
                Color.FromRgb (0xD9, 0x6E, 0x4E),
                Color.FromRgb (0xC1, 0x84, 0x2D),
                Color.FromRgb (0xDB, 0x6C, 0x72),
                Color.FromRgb (0xEB, 0x6F, 0x42),
                Color.FromRgb (0x9F, 0x8B, 0x81),
                Color.FromRgb (0x92, 0x94, 0x93),
                Color.FromRgb (0x76, 0x90, 0xDB),
                Color.FromRgb (0x85, 0x9A, 0x99),
                Color.FromRgb (0xE0, 0x79, 0x58),
                Color.FromRgb (0xBE, 0x87, 0x6D),
                Color.FromRgb (0xD5, 0x7E, 0x62),
                Color.FromRgb (0x5B, 0xA7, 0xDF),
                Color.FromRgb (0xCC, 0x91, 0x2A),
                Color.FromRgb (0xF5, 0x6F, 0x76),
                Color.FromRgb (0x7B, 0xA7, 0xA7),
                Color.FromRgb (0xF1, 0x7C, 0x54),
                Color.FromRgb (0xA1, 0x9C, 0x87),
                Color.FromRgb (0xE5, 0x81, 0x61),
                Color.FromRgb (0xF2, 0x8A, 0x47),
                Color.FromRgb (0xEE, 0x88, 0x67),
                Color.FromRgb (0xA1, 0xA3, 0xA3),
                Color.FromRgb (0x8A, 0xA0, 0xE5),
                Color.FromRgb (0xC4, 0x9A, 0x7F),
                Color.FromRgb (0xD9, 0x9F, 0x36),
                Color.FromRgb (0x95, 0xAC, 0xAA),
                Color.FromRgb (0xEC, 0x88, 0x8B),
                Color.FromRgb (0xAE, 0xA7, 0x92),
                Color.FromRgb (0xE8, 0x90, 0x70),
                Color.FromRgb (0xF5, 0x8F, 0x6F),
                Color.FromRgb (0xD5, 0x8B, 0xDC),
                Color.FromRgb (0x6A, 0xC2, 0xF7),
                Color.FromRgb (0xEE, 0x9A, 0x7A),
                Color.FromRgb (0xF7, 0x98, 0x74),
                Color.FromRgb (0x8D, 0xBA, 0xDB),
                Color.FromRgb (0xBA, 0xB1, 0x9C),
                Color.FromRgb (0xB2, 0xB3, 0xB1),
                Color.FromRgb (0xD2, 0xA8, 0x9B),
                Color.FromRgb (0xA6, 0xBA, 0xBD),
                Color.FromRgb (0xEC, 0xB4, 0x3A),
                Color.FromRgb (0xFC, 0x98, 0x9F),
                Color.FromRgb (0xF7, 0xA1, 0x80),
                Color.FromRgb (0xED, 0xA7, 0x85),
                Color.FromRgb (0xFA, 0xA9, 0x83),
                Color.FromRgb (0xDD, 0xB3, 0xAF),
                Color.FromRgb (0xFA, 0xA6, 0xA7),
                Color.FromRgb (0xC8, 0xC0, 0xAD),
                Color.FromRgb (0xFA, 0xB0, 0x8F),
                Color.FromRgb (0x89, 0xD9, 0xFC),
                Color.FromRgb (0xA9, 0xCF, 0xE8),
                Color.FromRgb (0xBB, 0xCC, 0xCB),
                Color.FromRgb (0xFB, 0xB2, 0xB2),
                Color.FromRgb (0xFB, 0xB9, 0x97),
                Color.FromRgb (0xE2, 0xC2, 0xAF),
                Color.FromRgb (0xFC, 0xCA, 0x40),
                Color.FromRgb (0xFA, 0xBF, 0x82),
                Color.FromRgb (0xC9, 0xCA, 0xC9),
                Color.FromRgb (0xF8, 0xAC, 0xF8),
                Color.FromRgb (0xD4, 0xCD, 0xC2),
                Color.FromRgb (0xFC, 0xC2, 0x9D),
                Color.FromRgb (0xFC, 0xBE, 0xBA),
                Color.FromRgb (0xD2, 0xD3, 0xD0),
                Color.FromRgb (0xEC, 0xC9, 0xC6),
                Color.FromRgb (0xCA, 0xD9, 0xD7),
                Color.FromRgb (0xFD, 0xCA, 0xA5),
                Color.FromRgb (0xFE, 0xDB, 0x5B),
                Color.FromRgb (0xD8, 0xD8, 0xD4),
                Color.FromRgb (0xFD, 0xCA, 0xC9),
                Color.FromRgb (0xC3, 0xDF, 0xF1),
                Color.FromRgb (0xFE, 0xD2, 0xB1),
                Color.FromRgb (0xFD, 0xD6, 0xA1),
                Color.FromRgb (0xEE, 0xD7, 0xCA),
                Color.FromRgb (0xFB, 0xCB, 0xF7),
                Color.FromRgb (0xFE, 0xDB, 0xB6),
                Color.FromRgb (0xFE, 0xF5, 0x2C),
                Color.FromRgb (0xFD, 0xD6, 0xD4),
                Color.FromRgb (0xE2, 0xE2, 0xDC),
                Color.FromRgb (0xFE, 0xEC, 0x74),
                Color.FromRgb (0xFE, 0xE1, 0xBE),
                Color.FromRgb (0xED, 0xE5, 0xDC),
                Color.FromRgb (0xD9, 0xEC, 0xF8),
                Color.FromRgb (0xFB, 0xE3, 0xD4),
                Color.FromRgb (0xFD, 0xDD, 0xFA),
                Color.FromRgb (0xFE, 0xE7, 0xC6),
                Color.FromRgb (0xFE, 0xFA, 0x91),
                Color.FromRgb (0xFE, 0xEF, 0xCD),
                Color.FromRgb (0xFC, 0xEB, 0xEA),
                Color.FromRgb (0xFE, 0xF6, 0xDC),
                Color.FromRgb (0xFE, 0xFD, 0xE4),
                Color.FromRgb (0x35, 0x29, 0x24),
                Color.FromRgb (0x1A, 0x43, 0x25),
                Color.FromRgb (0x01, 0x49, 0x96),
                Color.FromRgb (0x86, 0x27, 0x16),
                Color.FromRgb (0x4D, 0x52, 0x3F),
                Color.FromRgb (0xEB, 0x0E, 0x0A),
                Color.FromRgb (0x00, 0x6A, 0xCC),
                Color.FromRgb (0x80, 0x34, 0xC1),
                Color.FromRgb (0xFD, 0x00, 0xFF),
                Color.FromRgb (0x08, 0x87, 0xEF),
                Color.FromRgb (0x76, 0x70, 0x56),
                Color.FromRgb (0xB8, 0x55, 0x3F),
                Color.FromRgb (0x35, 0x9F, 0xE1),
                Color.FromRgb (0xAA, 0x7E, 0x60),
                Color.FromRgb (0x01, 0xFD, 0x00),
                Color.FromRgb (0xAB, 0x93, 0x8A),
                Color.FromRgb (0xD3, 0x8C, 0x56),
                Color.FromRgb (0x77, 0xC0, 0xAC),
                Color.FromRgb (0xB9, 0xA6, 0x9E),
                Color.FromRgb (0xE6, 0xAB, 0x63),
                Color.FromRgb (0x9D, 0xCC, 0xA5),
                Color.FromRgb (0xD1, 0xB6, 0x91),
                Color.FromRgb (0xA6, 0xD9, 0xCF),
                Color.FromRgb (0xEA, 0xC9, 0x9D),
                Color.FromRgb (0xDF, 0xE2, 0xBC),
                Color.FromRgb (0xFC, 0xE8, 0xA2),
                Color.FromRgb (0xF9, 0xF2, 0xDE),
                Color.FromRgb (0x23, 0x0D, 0x1A),
                Color.FromRgb (0x02, 0x58, 0x1A),
                Color.FromRgb (0x66, 0x39, 0x13),
                Color.FromRgb (0x36, 0x6D, 0x66),
                Color.FromRgb (0x90, 0x5F, 0x2A),
                Color.FromRgb (0x51, 0x9E, 0x7E),
                Color.FromRgb (0xC0, 0x91, 0x52),
                Color.FromRgb (0x7F, 0xC3, 0xAE),
                Color.FromRgb (0xE0, 0xBF, 0x78),
                Color.FromRgb (0xDC, 0xE8, 0xD4),
                Color.FromRgb (0x65, 0x39, 0x12),
                Color.FromRgb (0x22, 0x69, 0x49),
                Color.FromRgb (0x90, 0x5E, 0x2B),
                Color.FromRgb (0x36, 0x87, 0x5F),
                Color.FromRgb (0x53, 0x9F, 0x81),
                Color.FromRgb (0xBB, 0x85, 0x4C),
                Color.FromRgb (0xD9, 0xB9, 0x6F),
                Color.FromRgb (0x9A, 0xCE, 0xC2),
                Color.FromRgb (0xDF, 0xEF, 0xDE),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
                Color.FromRgb (0xFF, 0xFF, 0xFF),
            }
        );

        bool m_disposed = false;
        public void Dispose ()
        {
            if (!m_disposed && m_should_dispose)
            {
                m_input.Dispose();
                m_disposed = true;
            }
        }
    }
}
