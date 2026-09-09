import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export enum ExcelValidationSeverity {
  Warning = 1,
  Fatal = 2
}

export interface ExcelValidationIssue {
  rowNumber?: number | null;
  column?: string | null;
  severity: ExcelValidationSeverity;
  message: string;
  formattedMessage: string;
}

export interface ValidatedExcelTransaction {
  rowNumber: number;
  date?: string | null;
  narration?: string | null;
  chequeRefNo?: string | null;
  valueDate?: string | null;
  drAmount?: number | null;
  crAmount?: number | null;
  closingBalance?: number | null;
  ledgerName?: string | null;
  bankName?: string | null;
  rawDate?: string | null;
  rawDrAmount?: string | null;
  rawCrAmount?: string | null;
  rawClosingBalance?: string | null;
  status: 'Valid' | 'Warning' | 'Invalid';
  issues: ExcelValidationIssue[];
  isDebit: boolean;
  isCredit: boolean;
  amount: number;
}

export interface ExcelValidationResult {
  success: boolean;
  errorMessage?: string | null;
  totalRows: number;
  validRows: number;
  warningRows: number;
  invalidRows: number;
  sampleRowsSkipped: number;
  hasOfficialTemplateSignature: boolean;
  isReadyForXmlGeneration: boolean;
  validationMessages: ExcelValidationIssue[];
  parsedTransactions: ValidatedExcelTransaction[];
  templateInfo: string;
}

@Injectable({
  providedIn: 'root'
})
export class ExcelToTallyService {
  private readonly baseUrl = '/api/excel-to-tally';

  constructor(private readonly http: HttpClient) {}

  downloadTemplate(): Observable<Blob> {
    return this.http.get(`${this.baseUrl}/template`, {
      responseType: 'blob'
    });
  }

  validateExcelFile(file: File): Observable<ExcelValidationResult> {
    const formData = new FormData();
    formData.append('file', file, file.name);
    return this.http.post<ExcelValidationResult>(`${this.baseUrl}/validate`, formData);
  }

  generateTallyXml(validationResult: ExcelValidationResult): Observable<Blob> {
    return this.http.post(`${this.baseUrl}/generate-xml`, validationResult, {
      responseType: 'blob'
    });
  }

  saveBlob(blob: Blob, fileName: string): void {
    const url = window.URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(() => {
      window.URL.revokeObjectURL(url);
    }, 1000);
  }
}
