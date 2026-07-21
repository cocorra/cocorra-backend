using System;
using System.Collections.Generic;
using System.Net;

namespace Cocorra.BLL.Base
{
    /// <summary>
    /// A <see cref="Response{T}"/> specialization for paginated list endpoints.
    /// The page items live in the inherited <c>Data</c> property; the pagination
    /// metadata (including the computed <see cref="TotalPages"/> and
    /// <see cref="HasNextPage"/>) is promoted to first-class, strongly-typed fields
    /// so clients no longer have to compute them from a loosely-typed <c>Meta</c> bag.
    /// </summary>
    public class PagedResponse<T> : Response<IEnumerable<T>>
    {
        public int TotalCount { get; set; }
        public int CurrentPage { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
        public bool HasNextPage { get; set; }
        public bool HasPreviousPage { get; set; }

        public PagedResponse() { }

        public PagedResponse(
            IEnumerable<T> data,
            int totalCount,
            int currentPage,
            int pageSize,
            string message = "Operation Successful")
        {
            Data = data;
            TotalCount = totalCount;
            CurrentPage = currentPage;
            PageSize = pageSize;
            TotalPages = pageSize > 0 ? (int)Math.Ceiling(totalCount / (double)pageSize) : 0;
            HasNextPage = currentPage < TotalPages;
            HasPreviousPage = currentPage > 1;

            StatusCode = HttpStatusCode.OK;
            Succeeded = true;
            Message = message;
        }
    }
}
