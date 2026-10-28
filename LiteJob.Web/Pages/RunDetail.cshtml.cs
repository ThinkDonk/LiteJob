namespace LiteJob.Web.Pages;

public sealed class RunDetailModel : SecurePageModel
{
    public long Id { get; private set; }

    public void OnGet(long id)
    {
        Id = id;
    }
}
