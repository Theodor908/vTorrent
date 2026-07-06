using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.FileAllocator
{
    public class AllocationResult
    {
        public bool IsSuccess { get; set; }

        public AllocationError? ErrorType { get; set; }

        public List<string> AllocatedFilePaths { get; set; }

        public long TotalBytesAllocated { get; set; }

        public AllocationStrategy StrategyUsed { get; set; }

        public TimeSpan Duration { get; set; }

        public string ErrorMessage { get; set; }

        public List<string> Errors { get; set; }

        public long SpaceRequired { get; set; }

        public long SpaceAvailable { get; set; }

        public static AllocationResult Success(
            List<string> allocatedFiles,
            long totalBytes,
            AllocationStrategy strategy,
            TimeSpan duration)
        {
            return new AllocationResult
            {
                IsSuccess = true,
                AllocatedFilePaths = allocatedFiles ?? new List<string>(),
                TotalBytesAllocated = totalBytes,
                StrategyUsed = strategy,
                Duration = duration,
                Errors = new List<string>()
            };
        }

        public static AllocationResult Failure(
            AllocationError errorType,
            string errorMessage,
            long spaceRequired = 0,
            long spaceAvailable = 0)
        {
            return new AllocationResult
            {
                IsSuccess = false,
                ErrorType = errorType,
                ErrorMessage = errorMessage,
                Errors = new List<string> { errorMessage },
                AllocatedFilePaths = new List<string>(),
                SpaceRequired = spaceRequired,
                SpaceAvailable = spaceAvailable
            };
        }

        public static AllocationResult Failure(
            AllocationError errorType,
            List<string> errors)
        {
            return new AllocationResult
            {
                IsSuccess = false,
                ErrorType = errorType,
                ErrorMessage = errors.Count > 0 ? errors[0] : "Unknown error",
                Errors = errors ?? new List<string>(),
                AllocatedFilePaths = new List<string>()
            };
        }
    }
}
