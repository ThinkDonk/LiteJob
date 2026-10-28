namespace LiteJob.Web.Pages;

public sealed class TasksEditModel : SecurePageModel
{
    public long? TaskId { get; private set; }

    public void OnGet(long? id)
    {
        TaskId = id;
    }
}
