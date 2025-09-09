using Microsoft.AspNetCore.Mvc.RazorPages;
using Markdig;

namespace AgentSQL.Pages
{
    public class YellowpaperModel : PageModel
    {
        private readonly IWebHostEnvironment _environment;

        public YellowpaperModel(IWebHostEnvironment environment)
        {
            _environment = environment;
        }

        public string YellowpaperContent { get; set; } = string.Empty;

        public async Task OnGetAsync()
        {
            var yellowpaperPath = Path.Combine(_environment.WebRootPath, "yellowpaper.md");
            
            if (System.IO.File.Exists(yellowpaperPath))
            {
                var markdown = await System.IO.File.ReadAllTextAsync(yellowpaperPath);
                YellowpaperContent = Markdown.ToHtml(markdown);
            }
            else
            {
                YellowpaperContent = "<h1>Yellow Paper Not Found</h1><p>The yellow paper document could not be loaded.</p>";
            }
        }
    }
}