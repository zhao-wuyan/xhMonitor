namespace XhMonitor.Core.Common;

/// <summary>
/// 轻量级结果类型，用于表示可预期失败的操作结果。
/// </summary>
/// <typeparam name="T">成功时的值类型。</typeparam>
/// <typeparam name="TError">失败时的错误类型。</typeparam>
public readonly struct Result<T, TError>
{
    private readonly bool _isSuccess;
    private readonly T _value;
    private readonly TError _error;

    private Result(bool isSuccess, T value, TError error)
    {
        _isSuccess = isSuccess;
        _value = value;
        _error = error;
    }

    /// <summary>
    /// 是否成功。
    /// </summary>
    public bool IsSuccess => _isSuccess;

    /// <summary>
    /// 是否失败。
    /// </summary>
    public bool IsFailure => !_isSuccess;

    /// <summary>
    /// 成功时的值。失败时访问会抛出异常。
    /// </summary>
    public T Value => _isSuccess
        ? _value
        : throw new InvalidOperationException("Cannot access Value when result is failure.");

    /// <summary>
    /// 失败时的错误。成功时访问会抛出异常。
    /// </summary>
    public TError Error => !_isSuccess
        ? _error
        : throw new InvalidOperationException("Cannot access Error when result is success.");

    /// <summary>
    /// 创建成功结果。
    /// </summary>
    public static Result<T, TError> Success(T value) => new(true, value, default!);

    /// <summary>
    /// 创建失败结果。
    /// </summary>
    public static Result<T, TError> Failure(TError error) => new(false, default!, error);

    public static implicit operator Result<T, TError>(T value) => Success(value);

    public static implicit operator Result<T, TError>(TError error) => Failure(error);
}
