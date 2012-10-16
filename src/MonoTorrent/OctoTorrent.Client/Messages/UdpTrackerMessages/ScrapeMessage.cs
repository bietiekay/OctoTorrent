//
// ScrapeMessage.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2008 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

namespace OctoTorrent.Client.Messages.UdpTracker
{
    using System.Collections.Generic;
    using System.Linq;

    public class ScrapeMessage : UdpTrackerMessage
    {
        private readonly List<byte[]> _infoHashes;
        private long _connectionId;

        public ScrapeMessage()
            : this(0, 0, new List<byte[]>())
        {
        }

        public ScrapeMessage(int transactionId, long connectionId, List<byte[]> infoHashes)
            : base(2, transactionId)
        {
            _connectionId = connectionId;
            _infoHashes = infoHashes;
        }

        public override int ByteLength
        {
            get { return 8 + 4 + 4 + _infoHashes.Count*20; }
        }

        public List<byte[]> InfoHashes
        {
            get { return _infoHashes; }
        }

        public override void Decode(byte[] buffer, int offset, int length)
        {
            _connectionId = ReadLong(buffer, ref offset);
            if (Action != ReadInt(buffer, ref offset))
                throw new MessageException("Udp message decoded incorrectly");
            TransactionId = ReadInt(buffer, ref offset);
            while (offset <= (length - 20))
                _infoHashes.Add(ReadBytes(buffer, ref offset, 20));
        }

        public override int Encode(byte[] buffer, int offset)
        {
            var written = offset;

            written += Write(buffer, written, _connectionId);
            written += Write(buffer, written, Action);
            written += Write(buffer, written, TransactionId);
            written = _infoHashes.Aggregate(written, (current, t) => current + Write(buffer, current, t));

            return written - offset;
        }
    }
}