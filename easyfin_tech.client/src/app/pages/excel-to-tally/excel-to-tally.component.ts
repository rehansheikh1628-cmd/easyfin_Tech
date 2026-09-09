import { Component, ChangeDetectorRef } from '@angular/core';
import { finalize } from 'rxjs';
import {
  ExcelToTallyService,
  ExcelValidationResult,
  ValidatedExcelTransaction,
  ExcelValidationSeverity
} from '../../services/excel-to-tally.service';

type FilterTab = 'ALL' | 'VALID' | 'WARNING' | 'INVALID';

@Component({
  selector: 'app-excel-to-tally',
  templateUrl: './excel-to-tally.component.html',
  styleUrls: ['./excel-to-tally.component.css'],
  standalone: false
})
export class ExcelToTallyComponent {
  selectedFile: File | null = null;
  isValidating = false;
  isDownloadingTemplate = false;
  isGeneratingXml = false;
  errorMessage: string | null = null;
  successMessage: string | null = null;
  xmlSuccessMessage: string | null = null;
  xmlErrorMessage: string | null = null;

  validationResult: ExcelValidationResult | null = null;
  activeFilter: FilterTab = 'ALL';
  searchTerm = '';

  constructor(
    private readonly excelService: ExcelToTallyService,
    private readonly cdr: ChangeDetectorRef
  ) {}

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files && input.files.length > 0) {
      this.handleFile(input.files[0]);
    }
    input.value = '';
    this.cdr.markForCheck();
  }

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    if (event.dataTransfer?.files && event.dataTransfer.files.length > 0) {
      this.handleFile(event.dataTransfer.files[0]);
    }
  }

  handleFile(file: File): void {
    this.errorMessage = null;
    this.successMessage = null;
    this.xmlSuccessMessage = null;
    this.xmlErrorMessage = null;
    this.validationResult = null;

    const ext = file.name.split('.').pop()?.toLowerCase();
    if (ext !== 'xlsm' && ext !== 'xlsx' && ext !== 'xls') {
      this.errorMessage = `Invalid file format '.${ext}'. Please upload the official EasyFin Excel template (.xlsm) or .xlsx.`;
      this.cdr.markForCheck();
      return;
    }

    if (file.size > 50 * 1024 * 1024) {
      this.errorMessage = 'File size exceeds the 50MB limit.';
      this.cdr.markForCheck();
      return;
    }

    this.selectedFile = file;
    this.cdr.markForCheck();
    this.validateUploadedFile();
  }

  validateUploadedFile(): void {
    if (!this.selectedFile) return;

    this.isValidating = true;
    this.errorMessage = null;
    this.cdr.markForCheck();

    this.excelService.validateExcelFile(this.selectedFile)
      .pipe(
        finalize(() => {
          this.isValidating = false;
          this.cdr.markForCheck();
        })
      )
      .subscribe({
        next: (res) => {
          this.validationResult = res;

          if (!res.success && res.errorMessage) {
            this.errorMessage = res.errorMessage;
          }
          this.cdr.markForCheck();
        },
        error: (err) => {
          console.error('[ExcelToTally] Received validation error response:', err);
          if (err.error && typeof err.error === 'object' && err.error.errorMessage) {
            this.validationResult = err.error as ExcelValidationResult;
            this.errorMessage = err.error.errorMessage;
          } else {
            this.errorMessage = err.message || 'An unexpected error occurred while validating the Excel file.';
          }
          this.cdr.markForCheck();
        }
      });
  }

  downloadTemplate(): void {
    this.isDownloadingTemplate = true;
    this.errorMessage = null;
    this.cdr.markForCheck();

    this.excelService.downloadTemplate()
      .pipe(
        finalize(() => {
          this.isDownloadingTemplate = false;
          this.cdr.markForCheck();
        })
      )
      .subscribe({
        next: (blob) => {
          this.excelService.saveBlob(blob, 'EasyFin_Tally_Import_Template_v1.xlsm');
          this.successMessage = 'Official EasyFin .XLSM template downloaded successfully.';
          this.cdr.markForCheck();
          setTimeout(() => {
            this.successMessage = null;
            this.cdr.markForCheck();
          }, 4000);
        },
        error: (err) => {
          console.error('[ExcelToTally] Failed to download template:', err);
          this.errorMessage = 'Failed to download Excel template. Please try again.';
          this.cdr.markForCheck();
        }
      });
  }

  setFilter(filter: FilterTab): void {
    this.activeFilter = filter;
    this.cdr.markForCheck();
  }

  isFatal(severity: any): boolean {
    return severity === ExcelValidationSeverity.Fatal || severity === 2 || severity === 'Fatal';
  }

  isWarning(severity: any): boolean {
    return severity === ExcelValidationSeverity.Warning || severity === 1 || severity === 'Warning';
  }

  get filteredTransactions(): ValidatedExcelTransaction[] {
    if (!this.validationResult) return [];

    let list = this.validationResult.parsedTransactions || [];

    if (this.activeFilter === 'VALID') {
      list = list.filter((t) => t.status === 'Valid');
    } else if (this.activeFilter === 'WARNING') {
      list = list.filter((t) => t.status === 'Warning');
    } else if (this.activeFilter === 'INVALID') {
      list = list.filter((t) => t.status === 'Invalid');
    }

    if (this.searchTerm.trim()) {
      const q = this.searchTerm.toLowerCase();
      list = list.filter(
        (t) =>
          (t.narration && t.narration.toLowerCase().includes(q)) ||
          (t.ledgerName && t.ledgerName.toLowerCase().includes(q)) ||
          (t.bankName && t.bankName.toLowerCase().includes(q)) ||
          (t.chequeRefNo && t.chequeRefNo.toLowerCase().includes(q))
      );
    }

    return list;
  }

  generateTallyXml(): void {
    if (!this.validationResult || !this.validationResult.isReadyForXmlGeneration) {
      this.xmlErrorMessage = 'Cannot generate XML: Workbook contains fatal validation errors or is not ready.';
      this.cdr.markForCheck();
      return;
    }

    if (this.isGeneratingXml) {
      return; // Duplicate click protection
    }

    this.isGeneratingXml = true;
    this.xmlErrorMessage = null;
    this.xmlSuccessMessage = null;
    this.cdr.markForCheck();

    this.excelService.generateTallyXml(this.validationResult)
      .pipe(
        finalize(() => {
          this.isGeneratingXml = false;
          this.cdr.markForCheck();
        })
      )
      .subscribe({
        next: (blob) => {
          const now = new Date();
          const pad = (n: number) => n.toString().padStart(2, '0');
          const timestamp = `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}_${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`;
          const fileName = `EasyFin_Tally_Export_${timestamp}.xml`;
          this.excelService.saveBlob(blob, fileName);

          this.xmlSuccessMessage = 'Tally XML generated successfully.';
          this.cdr.markForCheck();
        },
        error: async (err) => {
          console.error('[ExcelToTally] Failed to generate XML:', err);
          let errorText = 'XML generation failed due to a server error. No XML file was generated.';

          if (err.error instanceof Blob) {
            try {
              const text = await err.error.text();
              const json = JSON.parse(text);
              if (json?.message) {
                errorText = json.message;
              }
            } catch {
              // fallback to default
            }
          } else if (err.error?.message) {
            errorText = err.error.message;
          } else if (err.message) {
            errorText = err.message;
          }

          this.xmlErrorMessage = errorText;
          this.cdr.markForCheck();
        }
      });
  }

  reset(): void {
    this.selectedFile = null;
    this.validationResult = null;
    this.errorMessage = null;
    this.successMessage = null;
    this.xmlSuccessMessage = null;
    this.xmlErrorMessage = null;
    this.activeFilter = 'ALL';
    this.searchTerm = '';
    this.isValidating = false;
    this.isGeneratingXml = false;
    this.cdr.markForCheck();
  }
}
