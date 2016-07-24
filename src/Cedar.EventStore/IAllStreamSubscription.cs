﻿namespace StreamStore
{
    using System;

    public interface IAllStreamSubscription : IDisposable
    {
        string Name { get; }

        long? LastCheckpoint { get; }
    }
}