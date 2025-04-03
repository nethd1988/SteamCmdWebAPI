using Microsoft.AspNetCore.Mvc;

namespace YourProjectName.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            return View(); // Trả về view mặc định Index
        }

        public IActionResult About()
        {
            ViewData["Message"] = "Trang giới thiệu ứng dụng.";
            return View();
        }

        public IActionResult Contact()
        {
            ViewData["Message"] = "Trang liên hệ.";
            return View();
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View();
        }
    }
}
