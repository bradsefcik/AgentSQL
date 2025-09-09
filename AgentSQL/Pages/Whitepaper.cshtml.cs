using Microsoft.AspNetCore.Mvc.RazorPages;
using Markdig;

namespace AgentSQL.Pages
{
    public class WhitepaperModel : PageModel
    {
        private readonly IWebHostEnvironment _environment;

        public WhitepaperModel(IWebHostEnvironment environment)
        {
            _environment = environment;
        }

        public string WhitepaperContent { get; set; } = string.Empty;

        public async Task OnGetAsync()
        {
            var whitepaperPath = Path.Combine(_environment.WebRootPath, "whitepaper.md");
            
            if (System.IO.File.Exists(whitepaperPath))
            {
                var markdown = await System.IO.File.ReadAllTextAsync(whitepaperPath);
                WhitepaperContent = Markdown.ToHtml(markdown);
            }
            else
            {
                WhitepaperContent = "<h1>Whitepaper Not Found</h1><p>The whitepaper document could not be loaded.</p>";
            }
        }
    }
}