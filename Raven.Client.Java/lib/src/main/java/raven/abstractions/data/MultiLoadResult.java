package raven.abstractions.data;

import raven.abstractions.json.RavenJObject;

import java.util.ArrayList;
import java.util.List;

public class MultiLoadResult {
  private List<RavenJObject> results;
  private List<RavenJObject> includes;


  /**
   * @return the results
   */
  public List<RavenJObject> getResults() {
    return results;
  }

  /**
   * @param results the results to set
   */
  public void setResults(List<RavenJObject> results) {
    this.results = results;
  }

  /**
   * @return the includes
   */
  public List<RavenJObject> getIncludes() {
    return includes;
  }

  /**
   * @param includes the includes to set
   */
  public void setIncludes(List<RavenJObject> includes) {
    this.includes = includes;
  }

  public MultiLoadResult() {
    super();
    results = new ArrayList<>();
    includes = new ArrayList<>();
  }


}
