using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Collective.GlobalReaderWriterLock;
namespace TestReader
{
    // run a bunch of these together to see they are non-exclusive
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Reader application running.  Wait for read lock...");
            using (GlobalReaderWriterLock.ForReading("MY_LOCK_NAME")) // all readers and writers must agree on this name (duh?)
            {
                Console.WriteLine("I have a read lock, get some reading done!");
                Thread.Sleep(5000);
            }
            Console.WriteLine("Reader is finished.");
        }
    }
}
