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
    internal static class ItemInfoEntryFactory
    {
        public static ItemInfoEntryBox Create(EndianBinaryReader reader, Box header)
        {
            ItemInfoEntryBox itemInfo = new ItemInfoEntryBox(reader, header);

            if (itemInfo.ItemType == ItemInfoEntryTypes.Mime)
            {
                return new MimeItemInfoEntryBox(reader, itemInfo);
            }
            else if (itemInfo.ItemType == ItemInfoEntryTypes.Uri)
            {
                return new UriItemEntryInfoBox(reader, itemInfo);
            }
            else
            {
                return itemInfo;
            }
        }
    }
}
