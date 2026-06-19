using Valuation.Api.Models;
using System.Collections.Generic;

namespace Valuation.Api.Services;

public interface IValuationRepository
{
    // Added '?' to make it nullable
    ValuationDocument? GetValuation(string id);
    IEnumerable<ValuationDocument> GetAll();
}