﻿////////////////////////////////////////////////////////////////////////
//
// This file is part of pdn-avif, a FileType plugin for Paint.NET
// that loads and saves AVIF images.
//
// Copyright (c) 2020 Nicholas Hayes
//
// This file is licensed under the MIT License.
// See LICENSE.txt for complete licensing and attribution information.
//
////////////////////////////////////////////////////////////////////////

namespace AvifFileType.AvifContainer
{
    internal sealed class PixelAspectRatioBox
        : ItemProperty
    {
        public PixelAspectRatioBox(EndianBinaryReader reader, Box header)
            : base(header)
        {
            this.HorizontalSpacing = reader.ReadUInt32();
            this.VerticalSpacing = reader.ReadUInt32();
        }

        public PixelAspectRatioBox(uint horizontalSpacing, uint verticalSpacing)
            : base(BoxTypes.PixelAspectRatio)
        {
            this.HorizontalSpacing = horizontalSpacing;
            this.VerticalSpacing = verticalSpacing;
        }

        public uint HorizontalSpacing { get; }

        public uint VerticalSpacing { get; }

        public override void Write(BigEndianBinaryWriter writer)
        {
            base.Write(writer);

            writer.Write(this.HorizontalSpacing);
            writer.Write(this.VerticalSpacing);
        }

        protected override ulong GetTotalBoxSize()
        {
            return base.GetTotalBoxSize() + sizeof(uint) + sizeof(uint);
        }
    }
}
