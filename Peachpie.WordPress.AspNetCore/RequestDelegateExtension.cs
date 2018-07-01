﻿using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCaching;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.Extensions.FileProviders;
using Pchp.Core;
using Peachpie.Web;
using Peachpie.WordPress.AspNetCore.Internal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Peachpie.WordPress.AspNetCore
{
    /// <summary>
    /// <see cref="IApplicationBuilder"/> extension for enabling WordPress.
    /// </summary>
    public static class RequestDelegateExtension
    {
        static void ShortUrlRule(RewriteContext context, IFileProvider files)
        {
            var req = context.HttpContext.Request;
            var subpath = req.Path.Value;
            if (subpath != "/")
            {
                if (subpath.IndexOf("wp-content/", StringComparison.Ordinal) != -1 ||
                    files.GetFileInfo(subpath).Exists ||
                    context.StaticFileProvider.GetFileInfo(subpath).Exists ||
                    subpath == "/favicon.ico") // 404
                {
                    // proceed to Static Files
                    return;
                }

                if (files.GetDirectoryContents(subpath).Exists)
                {
                    var lastchar = subpath[subpath.Length - 1];
                    if (lastchar != '/' && lastchar != '\\')
                    {
                        // redirect to the directory with leading slash:
                        context.HttpContext.Response.Redirect(req.PathBase + subpath + "/" + req.QueryString, true);
                        context.Result = RuleResult.EndResponse;
                    }

                    // proceed to default document
                    return;
                }
            }

            // everything else is handled by `index.php`
            req.Path = new PathString("/index.php");
            context.Result = RuleResult.SkipRemainingRules;
        }

        static IEnumerable<IWpPlugin> CollectPlugins(WordPressConfig config, params IWpPlugin[] plugins)
        {
            return plugins.Concat(config.Plugins ?? Array.Empty<IWpPlugin>());
        }

        /// <summary>
        /// Defines WordPress configuration constants and initializes runtime before proceeding to <c>index.php</c>.
        /// </summary>
        static void Apply(Context ctx, WordPressConfig config, params IWpPlugin[] plugins)
        {
            // see wp-config.php:

            // The name of the database for WordPress
            ctx.DefineConstant("DB_NAME", (PhpValue)config.DbName); // define('DB_NAME', 'wordpress');

            // MySQL database username
            ctx.DefineConstant("DB_USER", (PhpValue)config.DbUser); // define('DB_USER', 'root');

            // MySQL database password
            ctx.DefineConstant("DB_PASSWORD", (PhpValue)config.DbPassword); // define('DB_PASSWORD', 'password');

            // MySQL hostname
            ctx.DefineConstant("DB_HOST", (PhpValue)config.DbHost); // define('DB_HOST', 'localhost');

            // $peachpie-wp-loader : WpLoader
            ctx.Globals["peachpie_wp_loader"] = PhpValue.FromClass(new WpLoader(CollectPlugins(config, plugins)));
        }

        /// <summary>
        /// Installs WordPress middleware.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <param name="config">WordPress instance configuration.</param>
        /// <param name="path">Physical location of wordpress folder. Can be absolute or relative to the current directory.</param>
        public static IApplicationBuilder UseWordPress(this IApplicationBuilder app, WordPressConfig config, string path = "wordpress")
        {
            // wordpress root path:
            var root = System.IO.Path.GetFullPath(path);
            var fprovider = new PhysicalFileProvider(root);

            var cachepolicy = new WpResponseCachingPolicyProvider();
            var cachekey = new WpResponseCachingKeyProvider();

            // response caching:
            app.UseMiddleware<ResponseCachingMiddleware>(cachepolicy, cachekey);

            // url rewriting:
            app.UseRewriter(new RewriteOptions().Add(context => ShortUrlRule(context, fprovider)));

            // handling php files:
            app.UsePhp(new PhpRequestOptions()
            {
                ScriptAssembliesName = new[] { typeof(WpApp).Assembly.FullName },   // `WpApp` is compiled in PHP assembly WordPress.dll
                BeforeRequest = ctx => Apply(ctx, config, cachepolicy),
                RootPath = root,
            });

            // static files
            app.UseStaticFiles(new StaticFileOptions() { FileProvider = fprovider });

            return app;
        }
    }
}
