namespace BeatzOnKeys.Tests;

internal sealed class TestFailureException(string message) : Exception(message);

internal static class AssertEx
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new TestFailureException(message ?? "Expected condition to be true.");
        }
    }

    public static void False(bool condition, string? message = null) =>
        True(!condition, message ?? "Expected condition to be false.");

    public static void Null(object? value, string? message = null)
    {
        if (value is not null)
        {
            throw new TestFailureException(message ?? $"Expected null, got {value}.");
        }
    }

    public static T NotNull<T>(T? value, string? message = null) where T : class
    {
        if (value is null)
        {
            throw new TestFailureException(message ?? "Expected a non-null value.");
        }

        return value;
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new TestFailureException(message ?? $"Expected <{expected}>, got <{actual}>.");
        }
    }

    public static void NearlyEqual(double expected, double actual, double tolerance = 1e-9, string? message = null)
    {
        if (!double.IsFinite(expected) || !double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new TestFailureException(
                message ?? $"Expected <{expected:R}> ± {tolerance:R}, got <{actual:R}>.");
        }
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action, string? message = null)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new TestFailureException(
                message ?? $"Expected {typeof(TException).Name}, got {exception.GetType().Name}: {exception.Message}");
        }

        throw new TestFailureException(message ?? $"Expected {typeof(TException).Name}, but no exception was thrown.");
    }

    public static TException Throws<TException>(Action action, string? message = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new TestFailureException(
                message ?? $"Expected {typeof(TException).Name}, got {exception.GetType().Name}: {exception.Message}");
        }

        throw new TestFailureException(message ?? $"Expected {typeof(TException).Name}, but no exception was thrown.");
    }
}
