using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Cocorra.BLL.Base
{
    public class ResponseHandler
    {
        public Response<T> Deleted<T>(string message = "Deleted Successfully")
        {
            return new Response<T>()
            {
                StatusCode = HttpStatusCode.OK,
                Succeeded = true,
                Message = message,
                Data = default
            };
        }
        public Response<T> Success<T>(T entity, object? meta = null, string message = "Operation Successful")
        {
            return new Response<T>()
            {
                Data = entity,
                StatusCode = HttpStatusCode.OK,
                Succeeded = true,
                Message = message,
                Meta = meta
            };
        }
        public Response<T> Unauthorized<T>(string message = "Unauthorized")
        {
            return new Response<T>()
            {
                StatusCode = HttpStatusCode.Unauthorized,
                Succeeded = false,
                Message = message
            };
        }

        public Response<T> Forbidden<T>(string message = "Forbidden", object? meta = null)
        {
            return new Response<T>()
            {
                StatusCode = HttpStatusCode.Forbidden,
                Succeeded = false,
                Message = message,
                Meta = meta
            };
        }

        public Response<T> BadRequest<T>(string message, List<string> errors)
        {
            return new Response<T>()
            {
                StatusCode = HttpStatusCode.BadRequest,
                Succeeded = false,
                Message = message,
                Errors = errors
            };
        }
        public Response<T> BadRequest<T>(string message)
        {
            return new Response<T>()
            {
                StatusCode = HttpStatusCode.BadRequest,
                Succeeded = false,
                Message = message,
                Errors = new List<string>()
            };
        }
        
        public Response<T> BadRequest<T>(T data, string message)
        {
            return new Response<T>()
            {
                Data = data,
                StatusCode = HttpStatusCode.BadRequest,
                Succeeded = false,
                Message = message,
                Errors = new List<string>()
            };
        }
        public Response<T> UnprocessableEntity<T>(string message = "Validation Failed", List<string>? errors = null)
        {
            return new Response<T>()
            {
                StatusCode = HttpStatusCode.UnprocessableEntity, // 422
                Succeeded = false,
                Message = message,
                Errors = errors
            };
        }

        public Response<T> NotFound<T>(string message = "Not Found")
        {
            return new Response<T>()
            {
                StatusCode = HttpStatusCode.NotFound,
                Succeeded = false,
                Message = message
            };
        }

        public PagedResponse<T> Paginated<T>(
            IEnumerable<T> data,
            int totalCount,
            int currentPage,
            int pageSize,
            string message = "Operation Successful")
        {
            return new PagedResponse<T>(data, totalCount, currentPage, pageSize, message);
        }

        public Response<T> Created<T>(T entity, object? meta = null, string message = "Created Successfully")
        {
            return new Response<T>()
            {
                Data = entity,
                StatusCode = HttpStatusCode.Created,
                Succeeded = true,
                Message = message,
                Meta = meta
            };
        }
    }
}