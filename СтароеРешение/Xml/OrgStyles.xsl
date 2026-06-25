<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:ct="urn:BimHouse:CommonDataType" xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:cfg="urn:BimHouse:OrgUtilConfig" xmlns:msxsl="urn:schemas-microsoft-com:xslt" xmlns:bf='urn:BimHouse:XslFunctions'>

	<xsl:import href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/BimHouseFunctions.xsl"/>
	<xsl:output method="xml" version="1.0" encoding="UTF-8" indent="yes"/>
	
	<!--<xsl:template match="*[starts-with(@xsi:type,'ct:Тип.Базовый.Организация')]" mode="presenting">-->
	<xsl:template match="*[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.Базовый.Организация')]" mode="presenting">
		<xsl:element name="{name()}" namespace="{namespace-uri()}">
			<xsl:copy-of select="@*"/>
			<xsl:apply-templates select="./node()" mode="presenting"/>
			<xsl:element name="ct:Представления" namespace="urn:BimHouse:CommonDataType">
				<xsl:element name="ct:ВсеРеквизиты" namespace="urn:BimHouse:CommonDataType">
					<xsl:call-template name="CreateOrgTextPresentation">
						<xsl:with-param name="Org" select="."/>
					</xsl:call-template>
				</xsl:element>
				<xsl:element name="ДанныеСро" namespace="urn:BimHouse:CommonDataType">
					<xsl:call-template name="SroTextPresentation">
						<xsl:with-param name="Org" select="."/>
					</xsl:call-template>
				</xsl:element>
			</xsl:element>
		</xsl:element>
	</xsl:template>

	<xsl:template name="CreateOrgTextPresentation">
		<xsl:param name="Org"/>
		<xsl:if test="$Org/ct:Наименование/ct:Краткое != ''">
			<xsl:value-of select="$Org/ct:Наименование/ct:Краткое"/>
		</xsl:if>
		<xsl:if test="$Org/ct:ОГРН != ''">
			<xsl:text>, ОГРН&#160;</xsl:text>
			<xsl:value-of select="$Org/ct:ОГРН"/>
		</xsl:if>
		<xsl:choose>
			<xsl:when test="$Org/ct:ИНН != '' and $Org/ct:КПП != ''">
				<xsl:text>, ИНН/КПП&#160;</xsl:text>
				<xsl:value-of select="$Org/ct:ИНН"/>
				<xsl:text>/</xsl:text>
				<xsl:value-of select="$Org/ct:КПП"/>
			</xsl:when>
			<xsl:when test="$Org/ct:ИНН != ''">
				<xsl:text>, ИНН&#160;</xsl:text>
				<xsl:value-of select="$Org/ct:ИНН"/>
			</xsl:when>
		</xsl:choose>
		<xsl:variable name="Address1">
			<xsl:apply-templates select="$Org/ct:АдресЮридический" mode="presenting"/>
		</xsl:variable>
		<xsl:if test="$Address1/*/ct:Представления/ct:ПолныйАдрес != ''">
			<xsl:text>, юр. адрес: </xsl:text>
			<xsl:value-of select="$Address1/*/ct:Представления/ct:ПолныйАдрес"/>
		</xsl:if>
		<xsl:if test="$Address1/*/ct:Представления/ct:Контакты != ''">
			<xsl:text>, </xsl:text>
			<xsl:value-of select="$Address1/*/ct:Представления/ct:Контакты"/>
		</xsl:if>
		<xsl:variable name="Address2">
			<xsl:apply-templates select="$Org/ct:АдресПочтовый" mode="presenting"/>
		</xsl:variable>
		<xsl:if test="$Address2/*/ct:Представления/ct:ПолныйАдрес != ''">
			<xsl:text>, поч. адрес: </xsl:text>
			<xsl:value-of select="$Address2/*/ct:Представления/ct:ПолныйАдрес"/>
		</xsl:if>
		<xsl:if test="$Address2/*/ct:Представления/ct:Контакты != ''">
			<xsl:text>, </xsl:text>
			<xsl:value-of select="$Address2/*/ct:Представления/ct:Контакты"/>
		</xsl:if>
		<xsl:if test="$Org/ct:СРО">
			<xsl:for-each select="$Org/ct:СРО">
				<xsl:text>, свидетельство №&#160;</xsl:text>
				<xsl:value-of select="ct:Свидетельство/ct:Номер"/>
				<xsl:text> выдано </xsl:text>
				<xsl:value-of select="ct:ОрганизацияСРО"/>
			</xsl:for-each>
		</xsl:if>
	</xsl:template>

	<xsl:template name="SroTextPresentation">
		<xsl:param name="Org"/>
		<xsl:choose>
			<xsl:when test="not($Org/ct:СРО/ct:Свидетельство/ct:Номер)">
				<xsl:text> </xsl:text>
			</xsl:when>
			<xsl:otherwise>
				<xsl:text>Свидетельство № </xsl:text>
				<xsl:value-of select="$Org/ct:СРО/ct:Свидетельство/ct:Номер"/>
				<xsl:if test="$Org/ct:СРО/ct:Свидетельство/ct:Дата">
					<xsl:text> от </xsl:text>
					<xsl:value-of select="format-date($Org/ct:СРО/ct:Свидетельство/ct:Дата,'[D02].[M02].[Y04]')"/>
				</xsl:if>
			</xsl:otherwise>
		</xsl:choose>
	</xsl:template>

</xsl:stylesheet>