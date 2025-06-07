using Valuation.Api.Models;

namespace Valuation.Api.Services;

public interface IValuationRepository
{
    ValuationDocument GetValuation(string id);
    IEnumerable<ValuationDocument> GetAll();
}
