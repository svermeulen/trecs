using System;

namespace Trecs.Internal
{
    public interface ISimpleObservable
    {
        IDisposable Subscribe(Action handler);
        IDisposable Subscribe(SimpleReactiveBuffer buffer, Action handler);
    }

    public interface ISimpleObservable<T>
    {
        IDisposable Subscribe(Action<T> handler);
        IDisposable Subscribe(SimpleReactiveBuffer buffer, Action<T> handler);
    }

    public interface ISimpleObservable<T1, T2>
    {
        IDisposable Subscribe(Action<T1, T2> handler);
        IDisposable Subscribe(SimpleReactiveBuffer buffer, Action<T1, T2> handler);
    }
}
