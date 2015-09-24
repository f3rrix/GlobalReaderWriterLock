using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Collective.GlobalReaderWriterLock;

namespace TestWriter
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Writer application running.  Wait for write lock...");
            using (GlobalReaderWriterLock.ForWriting("MY_LOCK_NAME")) // all readers and writers must agree on this name (duh?)
            {
                Console.WriteLine("I have the write lock, now everyone has to wait until I get done writing.");
                Thread.Sleep(5000);
            }
            Console.WriteLine("Writer is finished.");

        }
    }
}
