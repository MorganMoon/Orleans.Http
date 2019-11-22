using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Orleans.Http
{
    internal class GrainRouter
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IClusterClient _clusterClient;
        private readonly ILogger _logger;
        private readonly Dictionary<GrainRoute, GrainInvoker> _routes = new Dictionary<GrainRoute, GrainInvoker>();

        public GrainRouter(IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
        {
            this._serviceProvider = serviceProvider;
            this._clusterClient = serviceProvider.GetRequiredService<IClusterClient>();
            this._logger = loggerFactory.CreateLogger<GrainRouter>();
        }

        public bool RegisterRoute(string pattern, string httpMethod, MethodInfo method)
        {
            var grainRoute = new GrainRoute(pattern, httpMethod);
            if (this._routes.ContainsKey(grainRoute)) return false;
            var grainInterfaceType = method.DeclaringType;
            var grainIdType = this.GetGrainIdType(grainInterfaceType);
            this._routes[grainRoute] = new GrainInvoker(this._serviceProvider, grainIdType, method);
            return true;
        }

        public Task Dispatch(HttpContext context)
        {
            var endpoint = (RouteEndpoint)context.GetEndpoint();
            var httpMethod = context.Request?.Method ?? "*";
            var pattern = endpoint.RoutePattern;
            var groundRoute = new GrainRoute(pattern.RawText, httpMethod);
            // At this point we are sure we have a patter and an invoker since a route was match for that particular endpoint
            var invoker = this._routes[groundRoute];

            IGrain grain = this.GetGrain(pattern, invoker.GrainType, invoker.GrainIdType, context);

            if (grain == null)
            {
                // We only faw here if the grainId is mal formed
                context.Response.StatusCode = (int)System.Net.HttpStatusCode.BadRequest;
                return Task.CompletedTask;
            }

            return invoker.Invoke(grain, context);
        }

        private IGrain GetGrain(RoutePattern pattern, Type grainType, GrainIdType grainIdType, HttpContext context)
        {
            try
            {
                var grainIdParameter = context.Request.RouteValues[Constants.GRAIN_ID];
                var grainIdExtensionParameter = context.Request.RouteValues.ContainsKey(Constants.GRAIN_ID_EXTENSION) ? context.Request.RouteValues[Constants.GRAIN_ID] : null;
                switch (grainIdType)
                {
                    case GrainIdType.String:
                        string stringId = (string)grainIdParameter;
                        return this._clusterClient.GetGrain(grainType, stringId);
                    case GrainIdType.Integer:
                        long integerId = Convert.ToInt64(grainIdParameter);
                        return this._clusterClient.GetGrain(grainType, integerId);
                    case GrainIdType.IntegerCompound:
                        return this._clusterClient.GetGrain(grainType, Convert.ToInt64(grainIdParameter), (string)grainIdExtensionParameter);
                    case GrainIdType.GuidCompound:
                        return this._clusterClient.GetGrain(grainType, Guid.Parse((string)grainIdParameter), (string)grainIdExtensionParameter);
                    default:
                        return this._clusterClient.GetGrain(grainType, Guid.Parse((string)grainIdParameter));
                }
            }
            catch (Exception exc)
            {
                this._logger.LogError(exc, $"Failure getting grain '{grainType.FullName} | {grainIdType}' for route '{pattern.RawText}': {exc.Message}");
                return null;
            }
        }

        private GrainIdType GetGrainIdType(Type grainInterfaceType)
        {
            var ifaces = grainInterfaceType.GetInterfaces();
            if (ifaces.Contains(typeof(IGrainWithGuidKey)))
            {
                return GrainIdType.Guid;
            }
            else if (ifaces.Contains(typeof(IGrainWithGuidCompoundKey)))
            {
                return GrainIdType.GuidCompound;
            }
            else if (ifaces.Contains(typeof(IGrainWithIntegerKey)))
            {
                return GrainIdType.Integer;
            }
            else if (ifaces.Contains(typeof(IGrainWithIntegerCompoundKey)))
            {
                return GrainIdType.IntegerCompound;
            }
            else
            {
                return GrainIdType.String;
            }
        }

        private struct GrainRoute : IEquatable<GrainRoute>
        {
            public string Pattern { get; }
            public string HttpMethod { get; }

            public GrainRoute(string pattern, string httpMethod)
            {
                Pattern = pattern;
                HttpMethod = httpMethod;
            }

            public override bool Equals(object obj)
            {
                if (obj == null)
                {
                    return false;
                }
                return obj is GrainRoute && Equals((GrainRoute)obj);
            }

            public override int GetHashCode()
            {
                return Pattern.GetHashCode() ^ HttpMethod.GetHashCode();
            }

            public bool Equals([AllowNull] GrainRoute other)
            {
                if(other == null)
                {
                    return false;
                }

                return Pattern == other.Pattern && HttpMethodEquals(HttpMethod, other.HttpMethod);
            }

            public static bool operator ==(GrainRoute x, GrainRoute y)
            {
                return x.Equals(y);
            }
            public static bool operator !=(GrainRoute x, GrainRoute y)
            {
                return !(x == y);
            }

            private bool HttpMethodEquals(string firstHttpMethod, string secondHttpMethod)
            {
                if(firstHttpMethod == "*" || secondHttpMethod == "*")
                {
                    return true;
                }

                return firstHttpMethod == secondHttpMethod;
            }
        }
    }

    internal enum GrainIdType
    {
        Guid = 0,
        String = 1,
        Integer = 2,
        GuidCompound = 3,
        IntegerCompound = 4
    }
}