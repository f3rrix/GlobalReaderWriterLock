using System;
using System.Collections.Generic;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Collective.GlobalReaderWriterLock
{
    // TL;DR: use like the following:
    //
    // using(GlobalReaderWriterLock.ForReading("SomeFixedNameAllYourAccessorsAgreeOn"))
    // {
    //     // perform reading here
    // }
    //
    // using(GlobalReaderWriterLock.ForWriting("SomeFixedNameAllYourAccessorsAgreeOn"))
    // {
    //     // perform writing here
    // }


    // appreciation to http://stackoverflow.com/questions/229565/what-is-a-good-pattern-for-using-a-global-mutex-in-c
    // and http://stackoverflow.com/questions/640122/is-there-a-global-named-reader-writer-lock

    // this (outer) class can be instantiated and disposed many times by many processes, 
    // and its synchronizing behavior will be consistent (because the synchronization and semaphore count are handled by the Windows system)
    public class GlobalReaderWriterLock : IDisposable
    {
        // highest concurrent amount of readers that should be allowed without waiting.
        private const int READER_CHIPS = 1000;

        protected string _hatName;
        protected Mutex _holdTheHat;
        protected Semaphore _chipsInTheHat;

        // These guys are only used to hold the temporary state of *this* object so Dispose will do the right thing
        // They do not affect the actual sync or count of the global resource managed by Windows
        bool _disposed = false;
        bool _iAmHoldingTheHat = false;
        int _numberOfChipsIAmHolding = 0;
        public GlobalReaderWriterLock(string nameOfThisLock)
        {
            _hatName = nameOfThisLock;

            // we need to make two native sync objects, a mutex and a semaphore
            // that are global so every process and user on the system can share them as long as they agree on the lock's name
            string mutexId = string.Format("Global\\Mut_{0}", _hatName);
            string semaphoreId = string.Format("Global\\Sem_{0}", _hatName);
            bool iDontCareWhetherItsNew;
            {
                MutexAccessRule allowEveryoneRule = new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.FullControl, AccessControlType.Allow);
                MutexSecurity securitySettings = new MutexSecurity();
                securitySettings.AddAccessRule(allowEveryoneRule);

                _holdTheHat = new Mutex(false, mutexId, out iDontCareWhetherItsNew, securitySettings);
            }
            {
                SemaphoreAccessRule allowEveryoneRule = new SemaphoreAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), SemaphoreRights.FullControl, AccessControlType.Allow);
                SemaphoreSecurity securitySettings = new SemaphoreSecurity();
                securitySettings.AddAccessRule(allowEveryoneRule);

                _chipsInTheHat = new Semaphore(READER_CHIPS, READER_CHIPS, semaphoreId, out iDontCareWhetherItsNew, securitySettings);
            }
        }
        public void AcquireReadLock()
        {
            // I want to take a reader chip from the hat, but first I have to be holding the hat itself (briefly)
            _iAmHoldingTheHat = _holdTheHat.WaitOne();
            // ok now I have the hat, and I'll wait until I can take a chip out of it.
            // If there aren't any chips, this call won't return until one of the other readers finishes and throws theirs back in
            _chipsInTheHat.WaitOne();
            _numberOfChipsIAmHolding++; // accounting for dispose
            // now I'm holding a reader chip! Let someone else hold the hat right away, while I go about my long read operation....
            if(_iAmHoldingTheHat) _holdTheHat.ReleaseMutex();
            _iAmHoldingTheHat = false; // accounting for dispose
        }
        public void ReleaseReadLock()
        {
            // I'm all done reading now, toss my chip back into the hat 
            // (I can do this from across the table even if someone else is holding it)
            _chipsInTheHat.Release();
            _numberOfChipsIAmHolding--; // accounting for dispose
        }
        public void AcquireWriteLock()
        {
            // I'm going to grab and bogart the hat until no readers are left, then do some long write operation
            // The point of this hat holding is that as long as I keep it, no NEW READERS can hold it.
            // So I am draining the world of readers as they naturally finish their reading business.
            // Which is what I need to have happen before being allowed to write safely!
            _iAmHoldingTheHat = _holdTheHat.WaitOne();
            // OK, I have the hat. But there might be some readers still holding chips, so I have to wait in that case.
            // (sempaphore note: you get the current chip count by doing a wait and release)
            _chipsInTheHat.WaitOne();
            while (_chipsInTheHat.Release() < (READER_CHIPS - 1))
            {
                // There was still at least one reader active, take a nap so we don't peg a CPU core
                Thread.Sleep(1);
                // check the count again by grabbing a chip (and then releasing it at the top of the while loop)
                _chipsInTheHat.WaitOne();
            }
            // OK, hat is full, we own the write lock!  Go do some long write operation....
        }
        public void ReleaseWriteLock()
        {
            // I'm all done writing now, so put the hat back down
            if(_iAmHoldingTheHat) _holdTheHat.ReleaseMutex();
            _iAmHoldingTheHat = false; // accounting for dispose
        }

        // Don't let the object die holding stuff
        private void Cleanup(bool disposing)
        {
            if(!_disposed)
            {
                // If the caller is sane, this should not occur. But check anyway
                if (_iAmHoldingTheHat) _holdTheHat.ReleaseMutex();
                _iAmHoldingTheHat = false;
                // If the caller is sane, this should not occur. But check anyway
                if (_numberOfChipsIAmHolding > 0)
                    _chipsInTheHat.Release(_numberOfChipsIAmHolding);
                _numberOfChipsIAmHolding = 0;
                _chipsInTheHat.Close();
                _chipsInTheHat = null;
                _holdTheHat.Close();
                _holdTheHat = null;
                _disposed = true;
                if (disposing)
                    GC.SuppressFinalize(this);
            }
        }
        public void Dispose()
        {
            Cleanup(true);
        }
        ~GlobalReaderWriterLock()
        {
            Cleanup(false);
        }

        // It would be sort of lame to have to call Acquire and Release and remember not to mess them up.
        // It would be much more elegant to be able to do something like the lock { } block.  
        // We can do this with an IDisposable helper object and the "using { } block"
        internal enum ReadOrWrite
        {
            None,
            Read,
            Write
        }
        // This object will live only for the lifetime of one thread holding a specific lock.
        // It will be auto-created by the below static functions, and constructing it grabs the lock.
        // As it is disposed by "using", it will release its held lock cleanly.
        public class LockHolder : IDisposable
        {
            GlobalReaderWriterLock _lock;
            ReadOrWrite _haveLock = ReadOrWrite.None;
            internal LockHolder(GlobalReaderWriterLock theLock, ReadOrWrite rw)
            {
                _lock = theLock;
                switch (rw)
                {
                    case ReadOrWrite.Read:
                        _lock.AcquireReadLock();
                        break;
                    case ReadOrWrite.Write:
                        _lock.AcquireWriteLock();
                        break;
                }
                _haveLock = rw;
            }
            private void Cleanup(bool disposing)
            {
                switch (_haveLock)
                {
                    case ReadOrWrite.Read:
                        _lock.ReleaseReadLock();
                        break;
                    case ReadOrWrite.Write:
                        _lock.ReleaseWriteLock();
                        break;
                    default:
                        // already ran cleanup, just return immediately instead of re-disposing below
                        return;
                }
                _haveLock = ReadOrWrite.None;
                _lock.Dispose();
                _lock = null;
                if (disposing)
                    GC.SuppressFinalize(this);
            }
            public void Dispose()
            {
                Cleanup(true);
            }
            ~LockHolder()
            {
                Cleanup(false);
            }
        }

        // OK, now some static methods to make using the helper class easy
        public static LockHolder ForReading(string nameOfTheLock)
        {
            return new LockHolder(new GlobalReaderWriterLock(nameOfTheLock), ReadOrWrite.Read);
        }
        public static LockHolder ForWriting(string nameOfTheLock)
        {
            return new LockHolder(new GlobalReaderWriterLock(nameOfTheLock), ReadOrWrite.Write);
        }
    }
}
