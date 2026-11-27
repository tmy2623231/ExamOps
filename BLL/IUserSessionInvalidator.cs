namespace ExamOps.BLL;

public interface IUserSessionInvalidator
{
    Task InvalidateUserAsync(long userId);
}
