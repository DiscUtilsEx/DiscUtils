﻿//
// Copyright (c) 2008-2009, Kenneth Bell
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.IO;

namespace DiscUtils.Ntfs
{
    internal class ClusterBitmap
    {
        private File _file;
        private Bitmap _bitmap;
        private static Random s_rng = new Random();

        private long _nextDataCluster;

        public ClusterBitmap(File file)
        {
            _file = file;
            NtfsAttribute attr = _file.GetAttribute(AttributeType.Data);
            _bitmap = new Bitmap(
                attr.OpenRaw(FileAccess.ReadWrite),
                Utilities.Ceil(file.Context.BiosParameterBlock.TotalSectors64, file.Context.BiosParameterBlock.SectorsPerCluster));
        }

        /// <summary>
        /// Allocates clusters from the disk
        /// </summary>
        /// <param name="count">The number of clusters to allocate</param>
        /// <param name="proposedStart">The proposed start cluster (or -1)</param>
        /// <param name="isMft"><c>true</c> if this attribute is the $MFT\$DATA attribute</param>
        /// <param name="total">The total number of clusters in the file, including this allocation</param>
        /// <returns>The list of cluster allocations</returns>
        public Tuple<long, long>[] AllocateClusters(long count, long proposedStart, bool isMft, long total)
        {
            List<Tuple<long, long>> result = new List<Tuple<long, long>>();

            long numFound = 0;

            long totalClusters = _file.Context.RawStream.Length / _file.Context.BiosParameterBlock.BytesPerCluster;

            if (isMft)
            {
                // First, try to extend the existing cluster run (if available)
                if (proposedStart >= 0)
                {
                    numFound += ExtendRun(count - numFound, result, proposedStart, totalClusters);
                }

                // The MFT grows sequentially across the disk
                if (numFound < count)
                {
                    numFound += FindClusters(count - numFound, result, 0, totalClusters, isMft, false, 0);
                }
            }
            else
            {
                // First, try to extend the existing cluster run (if available)
                if (proposedStart >= 0)
                {
                    numFound += ExtendRun(count - numFound, result, proposedStart, totalClusters);
                }

                // Try to find a contiguous range
                if (numFound < count)
                {
                    numFound += FindClusters(count - numFound, result, totalClusters / 8, totalClusters, isMft, true, total / 4);
                }

                if (numFound < count)
                {
                    numFound += FindClusters(count - numFound, result, totalClusters / 8, totalClusters, isMft, false, total / 4);
                }
                if (numFound < count)
                {
                    numFound = FindClusters(count - numFound, result, totalClusters / 16, totalClusters / 8, isMft, false, total / 4);
                }
                if (numFound < count)
                {
                    numFound = FindClusters(count - numFound, result, totalClusters / 32, totalClusters / 16, isMft, false, total / 4);
                }
                if (numFound < count)
                {
                    numFound = FindClusters(count - numFound, result, 0, totalClusters / 32, isMft, false, total / 4);
                }
            }

            if (numFound < count)
            {
                FreeClusters(result.ToArray());
                throw new IOException("Out of disk space");
            }

            return result.ToArray();
        }

        internal void FreeClusters(params Tuple<long, long>[] runs)
        {
            foreach (var run in runs)
            {
                _bitmap.MarkAbsentRange(run.First, run.Second);
            }
        }

        private long ExtendRun(long count, List<Tuple<long, long>> result, long start, long end)
        {
            long focusCluster = start;
            while (!_bitmap.IsPresent(focusCluster) && focusCluster < end && focusCluster - start < count)
            {
                ++focusCluster;
            }

            long numFound = focusCluster - start;

            if (numFound > 0)
            {
                _bitmap.MarkPresentRange(start, numFound);
                result.Add(new Tuple<long, long>(start, numFound));
            }

            return numFound;
        }

        /// <summary>
        /// Finds one or more free clusters in a range.
        /// </summary>
        /// <param name="count">The number of clusters required.</param>
        /// <param name="result">The list of clusters found (i.e. out param)</param>
        /// <param name="start">The first cluster in the range to look at</param>
        /// <param name="end">The last cluster in the range to look at (exclusive)</param>
        /// <param name="isMft">Indicates if the clusters are for the MFT</param>
        /// <param name="contiguous">Indicates if contiguous clusters are required</param>
        /// <param name="headroom">Indicates how many clusters to skip before next allocation, to prevent fragmentation</param>
        /// <returns>The number of clusters found in the range</returns>
        private long FindClusters(long count, List<Tuple<long, long>> result, long start, long end, bool isMft, bool contiguous, long headroom)
        {
            long numFound = 0;

            long focusCluster;
            if (isMft)
            {
                focusCluster = start;
            }
            else
            {
                if (_nextDataCluster < start || _nextDataCluster >= end)
                {
                    _nextDataCluster = start;
                }
                focusCluster = _nextDataCluster;
            }

            long numInspected = 0;
            while (numFound < count && focusCluster >= start && numInspected != end - start)
            {
                if (!_bitmap.IsPresent(focusCluster))
                {
                    // Start of a run...
                    long runStart = focusCluster;
                    ++focusCluster;

                    while (!_bitmap.IsPresent(focusCluster) && focusCluster - runStart < (count - numFound))
                    {
                        ++focusCluster;
                        ++numInspected;
                    }

                    if (!contiguous || (focusCluster - runStart) == (count - numFound))
                    {
                        _bitmap.MarkPresentRange(runStart, focusCluster - runStart);

                        result.Add(new Tuple<long, long>(runStart, focusCluster - runStart));
                        numFound += (focusCluster - runStart);
                    }
                }

                ++focusCluster;
                ++numInspected;
                if (focusCluster >= end)
                {
                    focusCluster = start;
                }
            }

            if (!isMft)
            {
                _nextDataCluster = focusCluster + headroom;
            }

            return numFound;
        }

    }
}
